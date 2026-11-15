import { useEffect, useRef, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import { Button } from '@/components/ui/button';
import { Card, CardDescription, CardTitle } from '@/components/ui/card';
import { CaptureIndicator } from '@/features/audio/CaptureIndicator';
import { DocumentsPanel } from '@/features/documents/DocumentsPanel';
import { LiveAssistant } from '@/features/copilot/LiveAssistant';
import { PracticeCard } from '@/features/practice/PracticePanel';
import { useApp } from '@/lib/app-context';
import { QUESTION_TYPE_LABELS, type Session } from '@/types/api';
import { DeleteSessionButton } from './DeleteSessionButton';
import { DuplicateSessionButton } from './DuplicateSessionButton';
import { SessionSettingsCard } from './SessionSettingsCard';
import { startLiveSession } from './start-live';

export function SessionPage() {
  const { sessionId = '' } = useParams();
  const { api, platform } = useApp();
  const navigate = useNavigate();
  const [session, setSession] = useState<Session | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const transitioning = useRef(false);

  useEffect(() => {
    let active = true;
    api.getSession(sessionId).then(
      (value) => {
        if (active) {
          setSession(value);
          setError(null);
        }
      },
      (err: Error) => { if (active) setError(err.message); },
    );
    return () => { active = false; };
  }, [api, sessionId]);

  async function run(action: () => Promise<Session>) {
    if (transitioning.current) return;
    transitioning.current = true;
    setBusy(true);
    setError(null);
    try {
      setSession(await action());
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Something went wrong.');
    } finally {
      transitioning.current = false;
      setBusy(false);
    }
  }

  async function exportPdf() {
    try {
      const blob = await api.downloadTranscriptPdf(sessionId);
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `${session?.title ?? 'transcript'}.pdf`;
      link.click();
      URL.revokeObjectURL(url);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Export failed.');
    }
  }

  if (error && (!session || session.id !== sessionId)) {
    return (
      <p role="alert" className="p-6 text-destructive">
        {error}
      </p>
    );
  }
  if (!session || session.id !== sessionId) {
    return <p className="p-6 text-muted-foreground">Loading…</p>;
  }

  return (
    <main className="mx-auto grid max-w-5xl gap-6 p-6 lg:grid-cols-[1fr_1.4fr]">
      <Card className="grid content-start gap-4">
        <div>
          <Link to="/" className="text-sm text-muted-foreground hover:underline">
            ← All sessions
          </Link>
          <CardTitle>{session.title}</CardTitle>
          <CardDescription>
            {session.answerLength} answers · {session.framework === 'None' ? 'no framework' : session.framework.toUpperCase()} ·{' '}
            {session.pinnedQuestionType ? `pinned: ${QUESTION_TYPE_LABELS[session.pinnedQuestionType]}` : 'auto-detect'}
            {' · '}Auto-answer: {session.autoAnswer ? 'on' : 'off'}
          </CardDescription>
        </div>
        <CaptureIndicator active={session.status === 'Live'} />
        <DocumentsPanel sessionId={session.id} documents={session.documents} onChange={(documents) => setSession({ ...session, documents })} />
        <div className="flex flex-wrap gap-2">
          {(session.status === 'Draft' || session.status === 'Ended') && (
            <Button
              disabled={busy}
              onClick={() =>
                void run(async () => {
                  let started: Session | undefined;
                  try {
                    await startLiveSession(api, platform, session.id, (value) => { started = value; });
                    await platform.showOverlay(session.id);
                    return started!;
                  } catch (err) {
                    if (started) setSession(started);
                    throw err;
                  }
                })
              }
            >
              {busy ? 'Starting…' : session.status === 'Ended' ? 'Start again' : 'Start session'}
            </Button>
          )}
          {session.status === 'Live' && (
            <Button
              variant="destructive"
              disabled={busy}
              onClick={() =>
                void run(async () => {
                  const results = await Promise.allSettled([platform.stopCapture(), platform.hideOverlay()]);
                  const failure = results.find((result) => result.status === 'rejected');
                  if (failure?.status === 'rejected') throw failure.reason;
                  return api.endSession(session.id);
                })
              }
            >
              {busy ? 'Ending…' : 'End session'}
            </Button>
          )}
          <Button asChild variant="outline">
            <Link to={`/sessions/${session.id}/transcript`}>View transcript</Link>
          </Button>
          <Button variant="outline" onClick={() => void exportPdf()}>
            Export PDF
          </Button>
          <DuplicateSessionButton session={session} />
          <DeleteSessionButton session={session} onDeleted={() => void navigate('/')} />
        </div>
        {error && (
          <p role="alert" className="text-sm text-destructive">
            {error}
          </p>
        )}
      </Card>
      <div className="grid content-start gap-6">
        <SessionSettingsCard session={session} onSaved={setSession} />
        {session.mode === 'MockInterview' ? (
          !busy && <PracticeCard key={session.status} sessionId={session.id} live={session.status === 'Live'} />
        ) : (
          <Card>
            <CardTitle className="mb-4">Live assistant</CardTitle>
            {!busy && <LiveAssistant
              key={`${session.id}-${session.status}`}
              sessionId={session.id}
              autoAnswer={session.autoAnswer}
              onAutoAnswerChanged={(autoAnswer) => setSession((current) => current && current.id === session.id ? { ...current, autoAnswer } : current)}
            />}
          </Card>
        )}
      </div>
    </main>
  );
}
