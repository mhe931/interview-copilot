import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { fakeApi, json, renderWithApp, sampleSession } from '@/test/helpers';
import { OverlayPage } from './OverlayPage';

describe('OverlayPage', () => {
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
