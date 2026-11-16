import { act, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createWebPlatform } from '@/platform';
import { FakeWebSocket } from '@/test/fakeWebSocket';
import { fakeApi, json, renderWithApp, sampleSession } from '@/test/helpers';
import { OverlayPage } from './OverlayPage';

describe('OverlayPage', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('renders answer deltas before completion', async () => {
    FakeWebSocket.reset();
    vi.stubGlobal('WebSocket', FakeWebSocket);
    const platform = createWebPlatform();
    await platform.setAuthTokens({ accessToken: 'test-token', refreshToken: 'test-refresh' });
    const { api } = fakeApi(() => json(sampleSession));
    renderWithApp(<OverlayPage />, { api, platform, route: '/overlay/s1', path: '/overlay/:sessionId' });
    await waitFor(() => expect(FakeWebSocket.instances.length).toBeGreaterThan(0));
    const socket = FakeWebSocket.last;
    act(() => socket.open());
    act(() => socket.emit({ id: 1, type: 'question.detected', data: { segmentId: 's1', text: 'What is your method?' } }));
    act(() => socket.emit({ id: 2, type: 'answer.delta', data: {
      segmentId: 's1', token: 'First part', version: 1, questionType: 'TechnicalKnowledge',
      origin: 'Prepared', sourceDocument: 'questions.txt',
    } }));

    expect(screen.getByText('First part')).toBeInTheDocument();
    expect(screen.getByText('Prepared · questions.txt')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Longer/ })).toBeDisabled();
  });

  it('starts paused when the session has auto-answer off', async () => {
    const { api } = fakeApi(() => json({ ...sampleSession, autoAnswer: false }));
    renderWithApp(<OverlayPage />, { api, route: '/overlay/s1', path: '/overlay/:sessionId' });

    expect(await screen.findByRole('button', { name: 'Resume auto-answer' })).toBeInTheDocument();
  });

  it('shows the error and disables auto-answer until the session can be read', async () => {
    let attempts = 0;
    const { api } = fakeApi(() => ++attempts === 1
      ? json({ detail: 'Session unavailable.' }, 500)
      : json({ ...sampleSession, autoAnswer: false }));
    renderWithApp(<OverlayPage />, { api, route: '/overlay/s1', path: '/overlay/:sessionId' });

    expect(await screen.findByRole('alert')).toHaveTextContent('Session unavailable.');
    expect(screen.getByRole('button', { name: 'Auto-answer unavailable' })).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Pause auto-answer' })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByRole('button', { name: 'Resume auto-answer' })).toBeEnabled();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});
