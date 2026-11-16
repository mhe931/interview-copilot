import { act, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createWebPlatform } from '@/platform';
import { FakeWebSocket } from '@/test/fakeWebSocket';
import { fakeApi, json, renderWithApp, sampleSession, sse } from '@/test/helpers';
import { LiveAssistant } from './LiveAssistant';

async function connected() {
  const platform = createWebPlatform();
  await platform.setAuthTokens({ accessToken: 'tok', refreshToken: 'r' });
  return platform;
}

async function connectedWithMic() {
  const platform = await connected();
  Object.defineProperty(platform, 'kind', { value: 'desktop' });
  vi.spyOn(platform, 'getCaptureStatus').mockResolvedValue({
    state: 'live',
    interviewer: true,
    mic: true,
    message: null,
  });
  return platform;
}

async function openSocket() {
  await waitFor(() => expect(FakeWebSocket.instances.length).toBeGreaterThan(0));
  const socket = FakeWebSocket.last;
  act(() => socket.open());
  return socket;
}

function send(socket: FakeWebSocket, type: string, data: unknown) {
  act(() => socket.emit({ id: 1, type, data }));
}

describe('LiveAssistant automatic answers', () => {
  beforeEach(() => {
    FakeWebSocket.reset();
    vi.stubGlobal('WebSocket', FakeWebSocket);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows the detected question and streams the automatic answer', async () => {
    const { api } = fakeApi(() => json({}));
    renderWithApp(<LiveAssistant sessionId="s1" />, { api, platform: await connected() });
    const socket = await openSocket();

    send(socket, 'question.detected', { segmentId: 'g1', text: 'Tell me about a time you led a team.' });
    expect(screen.getByText('Tell me about a time you led a team.')).toBeInTheDocument();
    expect(screen.getByText('Thinking…')).toBeInTheDocument();

    send(socket, 'answer.delta', { segmentId: 'g1', token: 'I led ', version: 1, questionType: 'Behavioral' });
    send(socket, 'answer.delta', { segmentId: 'g1', token: 'it.', version: 1, questionType: 'Behavioral' });
    expect(screen.getByText('I led it.')).toBeInTheDocument();
    expect(screen.getByText('Behavioral · v1')).toBeInTheDocument();

    send(socket, 'answer.done', { segmentId: 'g1', version: 1, questionType: 'Behavioral', answerId: 'a1' });
    expect(screen.getByRole('button', { name: /Shorter/ })).toBeEnabled();
  });

  it('stops only the latest automatic answer and keeps its partial text', async () => {
    const { api, fetchImpl } = fakeApi((url) => {
      if (url.endsWith('/answers/segment-2/cancel')) return new Response(null, { status: 204 });
      return json({});
    });
    renderWithApp(<LiveAssistant sessionId="s1" />, { api, platform: await connected() });
    const socket = await openSocket();

    send(socket, 'question.detected', { segmentId: 'segment-1', text: 'First question?' });
    send(socket, 'answer.delta', { segmentId: 'segment-1', token: 'Old partial' });
    send(socket, 'question.detected', { segmentId: 'segment-2', text: 'Latest question?' });
    send(socket, 'answer.delta', { segmentId: 'segment-2', token: 'Latest partial' });

    await userEvent.click(screen.getByRole('button', { name: 'Stop answer' }));

    const cancel = fetchImpl.mock.calls.find((call) => String(call[0]).endsWith('/answers/segment-2/cancel'));
    expect(cancel?.[1]?.method).toBe('POST');
    expect(await screen.findByText('Answer stopped.')).toBeInTheDocument();
    expect(screen.getByText('Latest partial')).toBeInTheDocument();
    expect(screen.queryByText('Old partial')).not.toBeInTheDocument();
  });

  it('labels a prepared answer and its source', async () => {
    const { api } = fakeApi(() => json({}));
    renderWithApp(<LiveAssistant sessionId="s1" />, { api, platform: await connected() });
    const socket = await openSocket();

    send(socket, 'question.detected', { segmentId: 'g1', text: 'What is your method?' });
    send(socket, 'answer.delta', {
      segmentId: 'g1', token: 'A prepared answer.', version: 1, questionType: 'TechnicalKnowledge',
      origin: 'Prepared', sourceDocument: 'questions.txt',
    });

    expect(screen.getByText('A prepared answer.')).toBeInTheDocument();
    expect(screen.getByText('Prepared · questions.txt')).toBeInTheDocument();
  });

  it('replaces the answer when a newer question is detected', async () => {
    const { api } = fakeApi(() => json({}));
    renderWithApp(<LiveAssistant sessionId="s1" />, { api, platform: await connected() });
    const socket = await openSocket();

    send(socket, 'question.detected', { segmentId: 'g1', text: 'First question here?' });
    send(socket, 'answer.delta', { segmentId: 'g1', token: 'old', version: 1, questionType: 'General' });
    send(socket, 'question.detected', { segmentId: 'g2', text: 'Second question here?' });
    send(socket, 'answer.delta', { segmentId: 'g2', token: 'new', version: 1, questionType: 'General' });

    expect(screen.queryByText('First question here?')).not.toBeInTheDocument();
    expect(screen.getByText('Second question here?')).toBeInTheDocument();
    expect(screen.getByText('new')).toBeInTheDocument();
  });

  it('shows an error event and ignores answer events when no automatic answer is running', async () => {
    const { api } = fakeApi(() => json({}));
    renderWithApp(<LiveAssistant sessionId="s1" />, { api, platform: await connected() });
    const socket = await openSocket();

    send(socket, 'answer.delta', { token: 'stray', version: 1, questionType: 'General' });
    send(socket, 'error', { message: 'Part of the conversation couldn’t be transcribed.' });
    expect(screen.queryByText('stray')).not.toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();

    send(socket, 'question.detected', { segmentId: 'g1', text: 'Why do you want this job?' });
    send(socket, 'error', { segmentId: 'g1', message: "Can't reach Ollama." });
    expect(screen.getByRole('alert')).toHaveTextContent("Can't reach Ollama.");
  });

  it('uses only an armed mic question and ignores ordinary candidate speech', async () => {
    const { api, fetchImpl } = fakeApi((_url, init) => init.method === 'POST'
      ? sse(['meta', '{"questionType":"Behavioral","version":1}'], ['delta', 'Mic answer'], ['done', ''])
      : json({}));
    renderWithApp(<LiveAssistant sessionId="s1" />, { api, platform: await connectedWithMic() });
    const socket = await openSocket();

    send(socket, 'transcript.segment', { speaker: 'Candidate', text: 'Tell me about a time you led a team.' });
    expect(fetchImpl).not.toHaveBeenCalled();

    await userEvent.click(screen.getByRole('button', { name: 'Ask from mic' }));
    expect(await screen.findByText(/Mic question mode is active/)).toBeInTheDocument();
    send(socket, 'transcript.segment', { speaker: 'Candidate', text: 'I led a team with six engineers.' });
    expect(await screen.findByText(/Ordinary speech was not sent/)).toBeInTheDocument();
    expect(fetchImpl).not.toHaveBeenCalled();

    await userEvent.click(screen.getByRole('button', { name: 'Ask from mic' }));
    expect(await screen.findByText(/Mic question mode is active/)).toBeInTheDocument();
    send(socket, 'transcript.segment', { speaker: 'Candidate', text: 'Tell me about a time you led a team.' });

    expect(await screen.findByText('Mic answer')).toBeInTheDocument();
    const post = fetchImpl.mock.calls.find((call) => call[1]?.method === 'POST')!;
    expect(JSON.parse(String(post[1]!.body))).toMatchObject({ question: 'Tell me about a time you led a team.' });
  });

  it('cancels mic question mode with Escape', async () => {
    const { api, fetchImpl } = fakeApi(() => json({}));
    renderWithApp(<LiveAssistant sessionId="s1" />, { api, platform: await connectedWithMic() });
    await openSocket();

    await userEvent.click(screen.getByRole('button', { name: 'Ask from mic' }));
    expect(await screen.findByText(/Mic question mode is active/)).toBeInTheDocument();
    await userEvent.keyboard('{Escape}');

    expect(await screen.findByText('Microphone question mode stopped.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Ask from mic' })).toBeEnabled();
    expect(fetchImpl).not.toHaveBeenCalled();
  });

  it('offers an explicit retry when the mic repeats a question just heard from meeting audio', async () => {
    const { api, fetchImpl } = fakeApi((_url, init) => init.method === 'POST'
      ? sse(['meta', '{"questionType":"Behavioral","version":1}'], ['delta', 'Retried answer'], ['done', ''])
      : json({}));
    renderWithApp(<LiveAssistant sessionId="s1" />, { api, platform: await connectedWithMic() });
    const socket = await openSocket();
    const question = 'Tell me about a time you led a team.';

    send(socket, 'question.detected', { segmentId: 'system-1', text: question });
    await userEvent.click(screen.getByRole('button', { name: 'Ask from mic' }));
    expect(await screen.findByText(/Mic question mode is active/)).toBeInTheDocument();
    send(socket, 'transcript.segment', { speaker: 'Candidate', text: question });

    expect(await screen.findByRole('button', { name: 'Answer anyway' })).toBeInTheDocument();
    expect(fetchImpl).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole('button', { name: 'Answer anyway' }));
    expect(await screen.findByText('Retried answer')).toBeInTheDocument();
    const post = fetchImpl.mock.calls.find((call) => call[1]?.method === 'POST')!;
    expect(JSON.parse(String(post[1]!.body))).toMatchObject({ question });
  });

  it('does not double up with the events a manual answer also publishes', async () => {
    const { api, fetchImpl } = fakeApi(() => sse(['meta', '{"questionType":"Behavioral","version":1}'], ['delta', 'Typed'], ['done', '']));
    renderWithApp(<LiveAssistant sessionId="s1" />, { api, platform: await connected() });
    const socket = await openSocket();

    await userEvent.type(screen.getByLabelText('Question'), 'Typed question here');
    await userEvent.click(screen.getByRole('button', { name: 'Answer' }));
    expect(await screen.findByText('Typed')).toBeInTheDocument();
    const post = fetchImpl.mock.calls.find((call) => call[1]?.method === 'POST')!;
    const { requestId } = JSON.parse(String(post[1]!.body)) as { requestId: string };
    send(socket, 'answer.started', { requestId, question: 'Typed question here' });
    expect(screen.getByText('Typed')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Shorter/ })).toBeEnabled();
    send(socket, 'answer.delta', { requestId, token: 'Typed', version: 1, questionType: 'Behavioral' });
    send(socket, 'answer.done', { requestId, version: 1, questionType: 'Behavioral', answerId: 'a1' });

    expect(screen.getByText('Typed')).toBeInTheDocument();
  });

  it('lets the user stop a streaming answer and preserves its partial text', async () => {
    const { api, fetchImpl } = fakeApi((url, init) => {
      if (url.endsWith('/api/preferences')) return json({ defaultLlmProvider: 'ollama' });
      return Promise.resolve(new Response(new ReadableStream<Uint8Array>({
        start(controller) {
          controller.enqueue(new TextEncoder().encode('event: meta\ndata: {"questionType":"Behavioral","version":1}\n\n'));
          controller.enqueue(new TextEncoder().encode('event: delta\ndata: Partial answer\n\n'));
          init.signal?.addEventListener('abort', () => controller.close(), { once: true });
        },
      }), { headers: { 'Content-Type': 'text/event-stream' } }));
    });
    renderWithApp(<LiveAssistant sessionId="s1" />, { api, platform: await connected() });
    await openSocket();

    await userEvent.type(screen.getByLabelText('Question'), 'A question?');
    await userEvent.click(screen.getByRole('button', { name: 'Answer' }));
    await screen.findByText('Partial answer');

    const post = fetchImpl.mock.calls.find((call) => call[1]?.method === 'POST')!;
    const signal = post[1]!.signal as AbortSignal;
    await userEvent.click(screen.getByRole('button', { name: 'Stop answer' }));

    expect(signal.aborted).toBe(true);
    expect(await screen.findByText('Answer stopped.')).toBeInTheDocument();
    expect(screen.getByText('Partial answer')).toBeInTheDocument();
  });

  it('streams a manual answer started in another window', async () => {
    const { api } = fakeApi(() => json({}));
    renderWithApp(<LiveAssistant sessionId="s1" />, { api, platform: await connected() });
    const socket = await openSocket();

    send(socket, 'answer.started', { requestId: 'manual-1', question: 'A typed question?' });
    send(socket, 'answer.delta', { requestId: 'manual-1', token: 'First', version: 1, questionType: 'Experience' });
    expect(screen.getByText('First')).toBeInTheDocument();
    send(socket, 'answer.delta', { requestId: 'manual-1', token: ' part', version: 1, questionType: 'Experience' });
    expect(screen.getByText('First part')).toBeInTheDocument();
    send(socket, 'answer.done', { requestId: 'manual-1', version: 1 });
    expect(screen.getByRole('button', { name: /Longer/ })).toBeEnabled();
  });

  it('shows failures for a manual answer started in another window', async () => {
    const { api } = fakeApi(() => json({}));
    renderWithApp(<LiveAssistant sessionId="s1" />, { api, platform: await connected() });
    const socket = await openSocket();

    send(socket, 'answer.started', { requestId: 'manual-1', question: 'A typed question?' });
    send(socket, 'error', { requestId: 'manual-1', message: 'Provider unavailable.' });

    expect(screen.getByRole('alert')).toHaveTextContent('Provider unavailable.');
  });

  it('pauses and resumes auto-answer through the API', async () => {
    const { api, fetchImpl } = fakeApi((url, init) => {
      const body = JSON.parse(String(init.body)) as { enabled: boolean };
      expect(url).toBe('http://api.test/api/sessions/s1/auto-answer');
      expect(init.method).toBe('PUT');
      return json({ ...sampleSession, autoAnswer: body.enabled });
    });
    renderWithApp(<LiveAssistant sessionId="s1" />, { api, platform: await connected() });
    await openSocket();

    await userEvent.click(screen.getByRole('button', { name: 'Pause auto-answer' }));
    expect(await screen.findByRole('button', { name: 'Resume auto-answer' })).toBeInTheDocument();
    expect(JSON.parse(String(fetchImpl.mock.calls[0]![1]!.body))).toEqual({ enabled: false });

    await userEvent.click(screen.getByRole('button', { name: 'Resume auto-answer' }));
    expect(await screen.findByRole('button', { name: 'Pause auto-answer' })).toBeInTheDocument();
  });

  it('follows autoanswer.state events from other windows and the initial value', async () => {
    const { api } = fakeApi(() => json({}));
    renderWithApp(<LiveAssistant sessionId="s1" autoAnswer={false} />, { api, platform: await connected() });
    const socket = await openSocket();
    expect(screen.getByRole('button', { name: 'Resume auto-answer' })).toBeInTheDocument();

    send(socket, 'autoanswer.state', { enabled: true });
    expect(screen.getByRole('button', { name: 'Pause auto-answer' })).toBeInTheDocument();
  });

  it('reports a failed pause request', async () => {
    const { api } = fakeApi(() => json({ detail: 'Session not found.' }, 404));
    renderWithApp(<LiveAssistant sessionId="s1" />, { api, platform: await connected() });
    await openSocket();

    await userEvent.click(screen.getByRole('button', { name: 'Pause auto-answer' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Session not found.');
    expect(screen.getByRole('button', { name: 'Pause auto-answer' })).toBeInTheDocument();
  });

  it('shows a detected question without answering while paused, and answers it on request', async () => {
    const { api, fetchImpl } = fakeApi(() => sse(['meta', '{"questionType":"Behavioral","version":1}'], ['delta', 'Manual.'], ['done', '']));
    renderWithApp(<LiveAssistant sessionId="s1" autoAnswer={false} />, { api, platform: await connected() });
    const socket = await openSocket();

    send(socket, 'question.detected', { segmentId: 'g1', text: 'Walk me through your resume.' });
    expect(screen.getByText('Walk me through your resume.')).toBeInTheDocument();
    expect(screen.queryByText('Thinking…')).not.toBeInTheDocument();
    expect(fetchImpl).not.toHaveBeenCalled();

    await userEvent.click(screen.getByRole('button', { name: 'Answer it' }));

    expect(await screen.findByText('Manual.')).toBeInTheDocument();
    const ask = fetchImpl.mock.calls.find((call) => call[1]?.method === 'POST')!;
    expect(JSON.parse(String(ask[1]!.body))).toMatchObject({ question: 'Walk me through your resume.' });
    expect(screen.queryByRole('button', { name: 'Answer it' })).not.toBeInTheDocument();
  });

  it('closes the old event subscription on unmount', async () => {
    const { api } = fakeApi(() => json({}));
    const view = renderWithApp(<LiveAssistant sessionId="s1" />, { api, platform: await connected() });
    const socket = await openSocket();
    send(socket, 'question.detected', { segmentId: 'g1', text: 'Old question' });
    expect(screen.getByText('Old question')).toBeInTheDocument();
    view.unmount();
    expect(socket.closed).toBe(true);
    expect(socket.onmessage).toBeNull();
  });

  it('notifies the session details when auto-answer changes', async () => {
    const changed = vi.fn();
    const { api } = fakeApi(() => json({ ...sampleSession, autoAnswer: false }));
    renderWithApp(<LiveAssistant sessionId="s1" onAutoAnswerChanged={changed} />, { api, platform: await connected() });
    const socket = await openSocket();
    send(socket, 'autoanswer.state', { enabled: false });
    expect(changed).toHaveBeenCalledWith(false);
  });
});
