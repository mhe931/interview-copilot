import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createWebPlatform } from '@/platform';
import { FakeWebSocket } from '@/test/fakeWebSocket';
import { fakeApi, json, renderWithApp, sampleSession } from '@/test/helpers';
import { SessionPage } from './SessionPage';
import { SessionsPage } from './SessionsPage';

const ended = { ...sampleSession, status: 'Ended' as const, autoAnswer: false };
const route = { route: '/sessions/s1', path: '/sessions/:sessionId' };

describe('duplicate and restart', () => {
  beforeEach(() => {
    FakeWebSocket.reset();
    vi.stubGlobal('WebSocket', FakeWebSocket);
  });
  afterEach(() => vi.unstubAllGlobals());

  it.each(['list', 'detail'])('duplicates from %s with an editable title and no capture', async (page) => {
    let resolveCreate!: (response: Response) => void;
    const { api, fetchImpl } = fakeApi((url, init) => {
      if (init.method === 'POST') return new Promise<Response>((resolve) => { resolveCreate = resolve; });
      if (url.endsWith('/api/sessions/copy')) return json({ ...ended, id: 'copy', title: 'A copy', status: 'Draft' });
      return json(url.endsWith('/api/sessions') ? [ended] : ended);
    });
    const platform = createWebPlatform();
    const capture = vi.spyOn(platform, 'startCapture');
    renderWithApp(page === 'list' ? <SessionsPage /> : <SessionPage />, { api, platform, ...(page === 'detail' ? route : {}) });
    await userEvent.click(await screen.findByRole('button', { name: /duplicate/i }));
    const dialog = await screen.findByRole('alertdialog');
    const title = within(dialog).getByLabelText('Title');
    await userEvent.clear(title);
    await userEvent.type(title, 'A copy');
    const form = title.closest('form')!;
    fireEvent.submit(form);
    fireEvent.submit(form);
    expect(fetchImpl.mock.calls.filter(([, init]) => init?.method === 'POST')).toHaveLength(1);
    const post = fetchImpl.mock.calls.find(([, init]) => init?.method === 'POST')!;
    expect(JSON.parse(String(post[1]!.body))).toMatchObject({ title: 'A copy', copyFromSessionId: 's1' });
    expect(capture).not.toHaveBeenCalled();
    await act(async () => resolveCreate(json({ ...ended, id: 'copy', title: 'A copy', status: 'Draft' }, 201)));
    if (page === 'list') {
      expect(await screen.findByText('navigated')).toBeInTheDocument();
    } else {
      expect(await screen.findByText('A copy')).toBeInTheDocument();
      expect(fetchImpl.mock.calls.some(([url]) => String(url).endsWith('/api/sessions/copy'))).toBe(true);
    }
    expect(fetchImpl.mock.calls.some(([url]) => String(url).endsWith('/start'))).toBe(false);
  });

  it('keeps duplicate dialog open and surfaces API errors', async () => {
    const { api } = fakeApi((_, init) => init.method === 'POST' ? json({ detail: 'Copy failed.' }, 400) : json(ended));
    renderWithApp(<SessionPage />, { api, ...route });
    await userEvent.click(await screen.findByRole('button', { name: /duplicate/i }));
    const dialog = await screen.findByRole('alertdialog');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Duplicate' }));
    const error = await within(dialog).findByRole('alert');
    expect(error).toHaveTextContent('Copy failed.');
    await waitFor(() => expect(error).toHaveFocus());
  });

  it('restarts the same session once with saved auto-answer off and stops capture before ending', async () => {
    let resolveStart!: (response: Response) => void;
    let resolveStop!: (value: Awaited<ReturnType<typeof platform.stopCapture>>) => void;
    const { api, fetchImpl } = fakeApi((url) => {
      if (url.endsWith('/start')) return new Promise<Response>((resolve) => { resolveStart = resolve; });
      if (url.endsWith('/end')) return json(ended);
      if (url.endsWith('/preferences')) return json({ defaultLlmProvider: 'ollama', defaultSpeechToTextProvider: 'whisper' });
      return json(ended);
    });
    const platform = createWebPlatform();
    const capture = vi.spyOn(platform, 'startCapture');
    vi.spyOn(platform, 'stopCapture').mockImplementation(() => new Promise((resolve) => { resolveStop = resolve; }));
    const hide = vi.spyOn(platform, 'hideOverlay');
    renderWithApp(<SessionPage />, { api, platform, ...route });
    const start = await screen.findByRole('button', { name: 'Start again' });
    expect(screen.getByText(/auto-answer: off/i)).toBeInTheDocument();
    fireEvent.click(start);
    fireEvent.click(start);
    expect(fetchImpl.mock.calls.filter(([url]) => String(url).endsWith('/start'))).toHaveLength(1);
    await act(async () => resolveStart(json({ ...ended, status: 'Live' })));
    const end = await screen.findByRole('button', { name: 'End session' });
    expect(capture).toHaveBeenCalledTimes(1);
    expect(capture).toHaveBeenCalledWith('s1');
    expect(screen.getByText(/auto-answer: off/i)).toBeInTheDocument();
    fireEvent.click(end);
    fireEvent.click(end);
    expect(fetchImpl.mock.calls.filter(([url]) => String(url).endsWith('/end'))).toHaveLength(0);
    await act(async () => resolveStop({ state: 'stopped', interviewer: false, mic: false, message: null }));
    await waitFor(() => expect(fetchImpl.mock.calls.filter(([url]) => String(url).endsWith('/end'))).toHaveLength(1));
    expect(hide).toHaveBeenCalledTimes(1);
    expect(await screen.findByRole('button', { name: 'Start again' })).toBeEnabled();
  });

  it('clears the previous runtime answer and subscription before restarting', async () => {
    const { api, fetchImpl } = fakeApi((url) => {
      if (url.endsWith('/start')) return json({ ...ended, status: 'Live', autoAnswer: true });
      if (url.endsWith('/preferences')) return json({ defaultLlmProvider: 'ollama', defaultSpeechToTextProvider: 'whisper' });
      return json({ ...ended, autoAnswer: true });
    });
    const platform = createWebPlatform();
    await platform.setAuthTokens({ accessToken: 'tok', refreshToken: 'r' });
    renderWithApp(<SessionPage />, { api, platform, ...route });
    await screen.findByRole('button', { name: 'Start again' });
    await waitFor(() => expect(FakeWebSocket.instances.length).toBeGreaterThan(0));
    const old = FakeWebSocket.last;
    act(() => {
      old.open();
      old.emit({ type: 'question.detected', data: { text: 'Old question' } });
      old.emit({ type: 'answer.delta', data: { token: 'Old answer' } });
    });
    expect(screen.getByText('Old answer')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Start again' }));
    await screen.findByRole('button', { name: 'End session' });
    expect(old.closed).toBe(true);
    expect(screen.queryByText('Old answer')).not.toBeInTheDocument();
    expect(fetchImpl.mock.calls.some(([, init]) => init?.method === 'DELETE')).toBe(false);
  });

  it('surfaces stop failures without ending the session', async () => {
    const { api, fetchImpl } = fakeApi(() => json({ ...ended, status: 'Live' }));
    const platform = createWebPlatform();
    vi.spyOn(platform, 'stopCapture').mockRejectedValue(new Error('Cannot stop capture.'));
    const hide = vi.spyOn(platform, 'hideOverlay');
    renderWithApp(<SessionPage />, { api, platform, ...route });
    await userEvent.click(await screen.findByRole('button', { name: 'End session' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Cannot stop capture.');
    expect(hide).toHaveBeenCalled();
    expect(fetchImpl.mock.calls.some(([url]) => String(url).endsWith('/end'))).toBe(false);
    expect(screen.getByRole('button', { name: 'End session' })).toBeEnabled();
  });

  it('shows hand-off errors and allows ending a session that already went live', async () => {
    const { api } = fakeApi((url) => {
      if (url.endsWith('/start')) return json({ ...ended, status: 'Live' });
      if (url.endsWith('/preferences')) return json({ defaultLlmProvider: 'openai', defaultSpeechToTextProvider: 'whisper' });
      return json(ended);
    });
    const platform = createWebPlatform();
    vi.spyOn(platform, 'getApiKey').mockRejectedValue(new Error('Keychain locked.'));
    const capture = vi.spyOn(platform, 'startCapture');
    renderWithApp(<SessionPage />, { api, platform, ...route });
    await userEvent.click(await screen.findByRole('button', { name: 'Start again' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Keychain locked.');
    expect(screen.getByRole('button', { name: 'End session' })).toBeEnabled();
    expect(capture).not.toHaveBeenCalled();
  });
});
