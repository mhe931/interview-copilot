import { useEffect, useState } from 'react';
import { useParams } from 'react-router';
import { Button } from '@/components/ui/button';
import { LiveAssistant } from '@/features/copilot/LiveAssistant';
import { useApp } from '@/lib/app-context';

/**
 * Rendered inside the desktop app's transparent, always-on-top overlay window (FR-17).
 * Window opacity is applied by the desktop shell from the opacity setting.
 */
export function OverlayPage() {
  const { sessionId = '' } = useParams();
  const { api } = useApp();
  // The overlay has no session object, so it reads the auto-answer setting once before showing the panel.
  const [result, setResult] = useState<{ sessionId: string; autoAnswer: boolean } | { sessionId: string; error: string } | null>(null);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let cancelled = false;
    api
      .getSession(sessionId)
      .then((session) => {
        if (!cancelled) setResult({ sessionId, autoAnswer: session.autoAnswer });
      })
      .catch((err: unknown) => {
        if (!cancelled) setResult({ sessionId, error: err instanceof Error ? err.message : 'Could not load the session.' });
      });
    return () => {
      cancelled = true;
    };
  }, [api, sessionId, attempt]);

  useEffect(() => {
    document.documentElement.classList.add('overlay');
    return () => document.documentElement.classList.remove('overlay');
  }, []);

  return (
    <div className="h-screen p-2">
      <div className="h-full overflow-auto rounded-xl border border-border bg-background/95 p-3 shadow-lg [-webkit-app-region:drag]">
        <div className="[-webkit-app-region:no-drag]">
          {result?.sessionId === sessionId && 'error' in result && (
            <div className="grid gap-2">
              <p role="alert" className="text-sm text-destructive">Could not load session settings: {result.error}</p>
              <div className="flex gap-2">
                <Button size="sm" variant="outline" disabled>Auto-answer unavailable</Button>
                <Button size="sm" variant="outline" onClick={() => {
                  setResult(null);
                  setAttempt((current) => current + 1);
                }}>Retry</Button>
              </div>
            </div>
          )}
          {result?.sessionId === sessionId && 'autoAnswer' in result && (
            <LiveAssistant sessionId={sessionId} compact autoAnswer={result.autoAnswer} />
          )}
        </div>
      </div>
    </div>
  );
}
