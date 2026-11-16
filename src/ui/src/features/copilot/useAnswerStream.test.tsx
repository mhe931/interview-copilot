import { act, renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AppProvider } from '@/lib/app-context';
import { createWebPlatform } from '@/platform';
import { fakeApi, json, sse } from '@/test/helpers';
import { useAnswerStream } from './useAnswerStream';

function setup(prefs: unknown | Response, answerResponse = sse(['meta', '{"questionType":"Behavioral","version":1}'], ['delta', 'Hi'], ['done', ''])) {
  const askKeys: (string | null)[] = [];
  const { api } = fakeApi((url, init) => {
    if (url.endsWith('/api/preferences')) return prefs instanceof Response ? prefs : json(prefs);
    askKeys.push(new Headers(init.headers).get('X-Llm-Api-Key'));
    return answerResponse;
  });
  const platform = createWebPlatform();
  const getApiKey = vi.spyOn(platform, 'getApiKey').mockImplementation((provider) => Promise.resolve(`key-${provider}`));
  const wrapper = ({ children }: { children: ReactNode }) => (
    <AppProvider api={api} platform={platform}>
      {children}
    </AppProvider>
  );
  return { wrapper, getApiKey, askKeys };
}

describe('useAnswerStream key header', () => {
  afterEach(() => vi.restoreAllMocks());

  it('sends the key of the account chat provider', async () => {
    const { wrapper, getApiKey, askKeys } = setup({ llmProvider: 'openai', defaultLlmProvider: 'ollama' });
    const { result } = renderHook(() => useAnswerStream('s1'), { wrapper });

    await result.current.ask('Why?');

    await waitFor(() => expect(result.current.answer.status).toBe('done'));
    expect(getApiKey).toHaveBeenCalledWith('openai');
    expect(askKeys).toEqual(['key-openai']);
  });

  describe('automatic answer identity', () => {
    it('ignores stale and unrelated answer events after a newer question', () => {
      const { wrapper } = setup({ defaultLlmProvider: 'google' });
      const { result } = renderHook(() => useAnswerStream('s1'), { wrapper });

      act(() => {
        result.current.beginAuto('First question?', 'first');
        result.current.beginAuto('Second question?', 'second');
        result.current.applyEvent({ id: 1, type: 'answer.delta', data: { segmentId: 'first', token: 'stale' } });
        result.current.applyEvent({ id: 2, type: 'answer.done', data: { segmentId: 'first' } });
        result.current.applyEvent({ id: 3, type: 'error', data: { message: 'unrelated' } });
        result.current.applyEvent({ id: 4, type: 'answer.delta', data: { segmentId: 'second', token: 'fresh' } });
      });

      expect(result.current.answer.question).toBe('Second question?');
      expect(result.current.answer.text).toBe('fresh');
      expect(result.current.answer.status).toBe('streaming');
    });
  });

  it('uses the configured default when no provider is chosen', async () => {
    const { wrapper, getApiKey } = setup({ llmProvider: null, defaultLlmProvider: 'google' });
    const { result } = renderHook(() => useAnswerStream('s1'), { wrapper });

    await result.current.ask('Why?');

    expect(getApiKey).toHaveBeenCalledWith('google');
  });

  it('falls back to Ollama when preferences cannot be read', async () => {
    const { wrapper, getApiKey } = setup(new Response(null, { status: 500 }));
    const { result } = renderHook(() => useAnswerStream('s1'), { wrapper });

    await result.current.ask('Why?');

    expect(getApiKey).toHaveBeenCalledWith('ollama');
  });

  it('does not send an answer request if unmounted while reading the key', async () => {
    const { wrapper, getApiKey, askKeys } = setup({ defaultLlmProvider: 'openai' });
    let resolveKey!: (key: string | null) => void;
    getApiKey.mockImplementation(() => new Promise((resolve) => { resolveKey = resolve; }));
    const { result, unmount } = renderHook(() => useAnswerStream('s1'), { wrapper });
    const pending = result.current.ask('Why?');
    await waitFor(() => expect(resolveKey).toBeDefined());
    unmount();
    resolveKey('key');
    await pending;
    expect(askKeys).toEqual([]);
  });

  it('keeps partial answer text when the stream times out and records display timing', async () => {
    const debug = vi.spyOn(console, 'debug').mockImplementation(() => undefined);
    const { wrapper } = setup(
      { defaultLlmProvider: 'ollama' },
      sse(['meta', '{"questionType":"Behavioral","version":1}'], ['delta', 'Useful partial answer'], ['error', 'Answer streaming stalled.']),
    );
    const { result } = renderHook(() => useAnswerStream('s1'), { wrapper });

    await act(async () => result.current.ask('Why this company?'));

    expect(result.current.answer.text).toBe('Useful partial answer');
    expect(result.current.answer.status).toBe('error');
    expect(result.current.answer.error).toBe('Answer streaming stalled.');
    expect(debug).toHaveBeenCalledWith('[answer-timing]', expect.objectContaining({ stage: 'first-displayed' }));
  });
});
