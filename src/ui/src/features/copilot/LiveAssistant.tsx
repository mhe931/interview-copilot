import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { Mic, Pause, Play, Square } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/form';
import { useApp } from '@/lib/app-context';
import { AnswerCard } from './AnswerCard';
import { useAnswerStream } from './useAnswerStream';
import { useSessionEvents, type SessionEvent } from './useSessionEvents';

/**
 * The live answer panel. Questions the interviewer asks are detected on the backend and answered
 * automatically (#22); typing or pasting a question here still works, and so do Regenerate/Shorter/Longer.
 */
export function LiveAssistant({
  sessionId,
  compact = false,
  autoAnswer: initialAutoAnswer = true,
  onAutoAnswerChanged,
}: {
  sessionId: string;
  compact?: boolean;
  /** Current auto-answer setting of the session; kept in sync afterwards through `autoanswer.state` events. */
  autoAnswer?: boolean;
  onAutoAnswerChanged?: (enabled: boolean) => void;
}) {
  const { api, platform } = useApp();
  const { answer, ask, cancel, canCancel, beginAuto, beginExternal, applyEvent } = useAnswerStream(sessionId);
  const [question, setQuestion] = useState('');
  const [autoAnswer, setAutoAnswer] = useState(initialAutoAnswer);
  const [pausedQuestion, setPausedQuestion] = useState<string | null>(null);
  const [toggleError, setToggleError] = useState<string | null>(null);
  const [micQuestionActive, setMicQuestionActive] = useState(false);
  const [micMessage, setMicMessage] = useState<string | null>(null);
  const [micError, setMicError] = useState<string | null>(null);
  const [duplicateMicQuestion, setDuplicateMicQuestion] = useState<string | null>(null);
  const micQuestionActiveRef = useRef(false);
  const lastSystemQuestion = useRef<{ text: string; receivedAt: number } | null>(null);

  const setMicMode = useCallback((active: boolean) => {
    micQuestionActiveRef.current = active;
    setMicQuestionActive(active);
  }, []);

  const cancelMicQuestion = useCallback(() => {
    setMicMode(false);
    setMicMessage('Microphone question mode stopped.');
  }, [setMicMode]);

  const startMicQuestion = useCallback(async () => {
    if (micQuestionActiveRef.current) return;
    setMicMessage(null);
    setMicError(null);
    setDuplicateMicQuestion(null);
    if (platform.kind !== 'desktop') {
      setMicError('Ask from mic is available in the desktop app during an active session.');
      return;
    }

    try {
      const capture = await platform.getCaptureStatus();
      if (capture.state !== 'live' || !capture.mic) {
        setMicError('Microphone capture is not active. Start or reconnect the session microphone, then try again.');
        return;
      }

      setMicMode(true);
      setMicMessage('Mic question mode is active. Repeat one question; only the next mic segment will be used. Press Escape to cancel.');
    } catch (err) {
      setMicError(err instanceof Error ? err.message : 'Could not check microphone capture.');
    }
  }, [platform, setMicMode]);

  useEffect(() => {
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape' && micQuestionActiveRef.current) {
        event.preventDefault();
        cancelMicQuestion();
        return;
      }

      const target = event.target;
      const editingText = target instanceof HTMLElement
        && (target.isContentEditable || target.closest('input, textarea, select'));
      if (event.altKey && event.shiftKey && event.key.toLowerCase() === 'm' && !editingText) {
        event.preventDefault();
        if (micQuestionActiveRef.current) {
          cancelMicQuestion();
        } else {
          void startMicQuestion();
        }
      }
    }

    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [cancelMicQuestion, startMicQuestion]);

  const onEvent = useCallback(
    (event: SessionEvent) => {
      const data = (event.data ?? {}) as { text?: string; speaker?: string; enabled?: boolean; segmentId?: string; question?: string; requestId?: string };
      if (event.type === 'autoanswer.state') {
        const enabled = Boolean(data.enabled);
        setAutoAnswer(enabled);
        onAutoAnswerChanged?.(enabled);
      } else if (event.type === 'question.detected' && data.text && data.segmentId) {
        lastSystemQuestion.current = { text: data.text, receivedAt: Date.now() };
        if (autoAnswer) {
          setPausedQuestion(null);
          beginAuto(data.text, data.segmentId);
        } else {
          setPausedQuestion(data.text);
        }
      } else if (event.type === 'transcript.segment' && data.speaker === 'Candidate' && data.text && micQuestionActiveRef.current) {
        setMicMode(false);
        const spokenQuestion = data.text.trim();
        if (!looksLikeQuestion(spokenQuestion)) {
          setMicMessage('No question was detected in that mic segment. Ordinary speech was not sent; try Ask from mic again.');
          return;
        }

        const recentQuestion = lastSystemQuestion.current;
        if (recentQuestion
          && Date.now() - recentQuestion.receivedAt <= 8_000
          && normalizeQuestion(recentQuestion.text) === normalizeQuestion(spokenQuestion)) {
          setDuplicateMicQuestion(spokenQuestion);
          setMicMessage('The same question was just heard from meeting audio. Answer anyway to deliberately retry it.');
          return;
        }

        setMicMessage('Question captured from mic. Generating an answer…');
        void ask(spokenQuestion);
      } else if (event.type === 'answer.started' && data.question && data.requestId) {
        setPausedQuestion(null);
        beginExternal(data.question, data.requestId);
      } else {
        applyEvent(event);
      }
    },
    [autoAnswer, ask, beginAuto, beginExternal, applyEvent, onAutoAnswerChanged, setMicMode],
  );
  useSessionEvents(sessionId, onEvent);

  function submit(event: FormEvent) {
    event.preventDefault();
    if (question.trim()) {
      setPausedQuestion(null);
      void ask(question.trim());
    }
  }

  async function toggleAutoAnswer() {
    setToggleError(null);
    try {
      const session = await api.setAutoAnswer(sessionId, !autoAnswer);
      setAutoAnswer(session.autoAnswer);
      onAutoAnswerChanged?.(session.autoAnswer);
    } catch (err) {
      setToggleError(err instanceof Error ? err.message : 'Could not change auto-answer.');
    }
  }

  return (
    <section className={compact ? 'grid gap-3' : 'grid gap-4'}>
      <div className="flex items-center justify-between gap-2">
        <p className="text-sm text-muted-foreground">{autoAnswer ? 'Auto-answer is on.' : 'Auto-answer is paused.'}</p>
        <div className="flex flex-wrap justify-end gap-2">
          <Button
            type="button"
            size="sm"
            variant={micQuestionActive ? 'destructive' : 'outline'}
            aria-keyshortcuts="Alt+Shift+M"
            onClick={() => micQuestionActive ? cancelMicQuestion() : void startMicQuestion()}
          >
            {micQuestionActive ? <Square className="size-3.5" /> : <Mic className="size-3.5" />}
            {micQuestionActive ? 'Stop listening' : 'Ask from mic'}
          </Button>
          <Button size="sm" variant="outline" onClick={() => void toggleAutoAnswer()}>
            {autoAnswer ? <Pause className="size-3.5" /> : <Play className="size-3.5" />}
            {autoAnswer ? 'Pause auto-answer' : 'Resume auto-answer'}
          </Button>
        </div>
      </div>
      <p className="text-xs text-muted-foreground">Keyboard: Tab to Ask from mic, then Enter. Shortcut: Alt+Shift+M; Escape stops listening.</p>
      {micMessage && <p role="status" aria-live="polite" className="text-sm text-muted-foreground">{micMessage}</p>}
      {micError && <p role="alert" className="text-sm text-destructive">{micError}</p>}
      {duplicateMicQuestion && (
        <div className="grid gap-2 rounded-md border border-border p-3">
          <p className="text-sm">{duplicateMicQuestion}</p>
          <Button
            type="button"
            size="sm"
            className="w-fit"
            onClick={() => {
              const retry = duplicateMicQuestion;
              setDuplicateMicQuestion(null);
              setMicMessage('Retrying the mic question.');
              void ask(retry);
            }}
          >
            Answer anyway
          </Button>
        </div>
      )}
      {toggleError && (
        <p role="alert" className="text-sm text-destructive">
          {toggleError}
        </p>
      )}
      <form onSubmit={submit} className="flex gap-2">
        <Input aria-label="Question" placeholder="Type or paste the question you were asked" value={question} onChange={(e) => setQuestion(e.target.value)} />
        <Button type="submit">Answer</Button>
      </form>
      {pausedQuestion && (
        <div className="grid gap-2 rounded-md border border-border p-3">
          <p className="text-sm text-muted-foreground">{pausedQuestion}</p>
          <Button
            size="sm"
            className="w-fit"
            onClick={() => {
              const detected = pausedQuestion;
              setPausedQuestion(null);
              void ask(detected);
            }}
          >
            Answer it
          </Button>
        </div>
      )}
      <AnswerCard
        answer={answer}
        canCancel={canCancel}
        onCancel={cancel}
        onModify={(modifier) => void ask(answer.question, modifier, answer)}
      />
    </section>
  );
}

const QUESTION_OPENERS = [
  'how', 'what', 'why', 'when', 'where', 'which', 'who', 'can you', 'could you', 'would you',
  'will you', 'do you', 'did you', 'have you', 'are you', 'is there', 'tell me', 'describe',
  'walk me through', 'explain', 'give me an example', 'talk me through', 'share',
];

function looksLikeQuestion(utterance: string): boolean {
  const text = utterance.trim();
  if (text.split(/\s+/).length < 3) return false;
  if (text.endsWith('?')) return true;
  const lower = text.toLowerCase();
  return QUESTION_OPENERS.some((opener) => lower.startsWith(`${opener} `) || lower.includes(`. ${opener} `));
}

function normalizeQuestion(question: string): string {
  return question.toLocaleLowerCase().replace(/[^a-z0-9]/g, '');
}
