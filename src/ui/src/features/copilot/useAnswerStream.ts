import { useCallback, useEffect, useRef, useState } from 'react';
import type { SessionEvent } from './useSessionEvents';
import { useApp } from '@/lib/app-context';
import type { AnswerModifier, AnswerOrigin, QuestionType } from '@/types/api';

export interface AnswerState {
  question: string;
  text: string;
  questionType: QuestionType | null;
  version: number;
  origin: AnswerOrigin | null;
  sourceDocument: string | null;
  status: 'idle' | 'streaming' | 'done' | 'error' | 'cancelled';
  error: string | null;
}

const IDLE: AnswerState = { question: '', text: '', questionType: null, version: 0, origin: null, sourceDocument: null, status: 'idle', error: null };

/** Streams answers for one session; a new question cancels the previous stream (atlas/copilot-pipeline.md). */
export function useAnswerStream(sessionId: string, fallbackProvider = 'ollama') {
  const { api, platform } = useApp();
  const [answer, setAnswer] = useState<AnswerState>(IDLE);
  const [canCancel, setCanCancel] = useState(false);
  const controller = useRef<AbortController | null>(null);
  const activeEvent = useRef<{ segmentId: string } | { requestId: string } | null>(null);
  const localRequests = useRef(new Set<string>());
  const answerStartedAt = useRef<number | null>(null);
  const firstDisplayedLogged = useRef(false);

  useEffect(() => () => {
    controller.current?.abort();
    activeEvent.current = null;
    localRequests.current.clear();
  }, [sessionId]);

  useEffect(() => {
    if (answer.text.length > 0 && answerStartedAt.current !== null && !firstDisplayedLogged.current) {
      firstDisplayedLogged.current = true;
      logAnswerTiming('first-displayed', answerStartedAt.current);
    }
  }, [answer.text]);

  const beginTiming = useCallback(() => {
    answerStartedAt.current = performance.now();
    firstDisplayedLogged.current = false;
  }, []);

  const effectiveProvider = useCallback(async () => {
    try {
      const prefs = await api.getPreferences();
      return prefs.llmProvider || prefs.defaultLlmProvider || fallbackProvider;
    } catch {
      return fallbackProvider;
    }
  }, [api, fallbackProvider]);

  const ask = useCallback(
    async (question: string, modifier: AnswerModifier = 'None', previous?: AnswerState) => {
      controller.current?.abort();
      activeEvent.current = null;
      beginTiming();
      const abort = new AbortController();
      controller.current = abort;
      setCanCancel(true);
      const requestId = crypto.randomUUID();
      localRequests.current.add(requestId);
      setAnswer({ ...IDLE, question, status: 'streaming', version: previous?.version ?? 0 });

      // Manual answers of a session without a live key hand-off carry the key of the account's chat provider (#30).
      try {
        const llmApiKey = (await platform.getApiKey(await effectiveProvider())) ?? undefined;
        if (abort.signal.aborted) return;
        await api.ask(
          sessionId,
          {
            question,
            modifier,
            previousAnswer: previous?.text,
            previousVersion: previous?.version ?? 0,
            requestId,
            llmApiKey,
          },
          {
            onHeader: (header) => { if (!abort.signal.aborted) setAnswer((s) => ({ ...s, questionType: header.questionType, version: header.version, origin: header.origin ?? null, sourceDocument: header.sourceDocument ?? null })); },
            onToken: (token) => { if (!abort.signal.aborted) setAnswer((s) => ({ ...s, text: s.text + token })); },
            onError: (message) => { if (!abort.signal.aborted) setAnswer((s) => ({ ...s, status: 'error', error: message })); },
            onDone: () => {
              if (!abort.signal.aborted) {
                setCanCancel(false);
                if (answerStartedAt.current !== null) logAnswerTiming('display-complete', answerStartedAt.current);
                setAnswer((s) => ({ ...s, status: 'done' }));
              }
            },
          },
          abort.signal,
        );
      } catch (err) {
        if (!abort.signal.aborted) {
          setCanCancel(false);
          setAnswer((s) => ({ ...s, status: 'error', error: err instanceof Error ? err.message : 'The answer failed.' }));
        }
      }
    },
    [api, platform, sessionId, effectiveProvider, beginTiming],
  );

  const cancel = useCallback(async () => {
    if (controller.current && !controller.current.signal.aborted) {
      controller.current.abort();
      controller.current = null;
      setCanCancel(false);
      setAnswer((s) => s.status === 'streaming' ? { ...s, status: 'cancelled' } : s);
      return;
    }

    const current = activeEvent.current;
    if (!current || !('segmentId' in current)) return;
    try {
      await api.cancelAutomaticAnswer(sessionId, current.segmentId);
    } catch (err) {
      if (activeEvent.current === current) {
        setAnswer((s) => ({
          ...s,
          error: err instanceof Error ? `Could not stop the answer: ${err.message}` : 'Could not stop the answer.',
        }));
      }
      return;
    }

    if (activeEvent.current === current) {
      activeEvent.current = null;
      setCanCancel(false);
      setAnswer((s) => s.status === 'streaming' ? { ...s, status: 'cancelled' } : s);
    }
  }, [api, sessionId]);

  /** Starts showing an automatic answer for a detected question (#22); a newer one replaces it. */
  const beginAuto = useCallback((question: string, segmentId: string) => {
    controller.current?.abort();
    beginTiming();
    activeEvent.current = { segmentId };
    setCanCancel(true);
    setAnswer({ ...IDLE, question, status: 'streaming' });
  }, [beginTiming]);

  const beginExternal = useCallback((question: string, requestId: string) => {
    if (localRequests.current.has(requestId)) return;
    controller.current?.abort();
    beginTiming();
    activeEvent.current = { requestId };
    setCanCancel(false);
    setAnswer({ ...IDLE, question, status: 'streaming' });
  }, [beginTiming]);

  /**
   * Applies `answer.delta` / `answer.done` / `error` events of the automatic answer. Ignored unless one is in
   * progress, so the events a manual answer also publishes don't double up with its own stream.
   */
  const applyEvent = useCallback((event: SessionEvent) => {
    const data = (event.data ?? {}) as { token?: string; version?: number; questionType?: QuestionType; origin?: AnswerOrigin; sourceDocument?: string | null; message?: string; segmentId?: string; requestId?: string };
    const current = activeEvent.current;
    if (!current || ('segmentId' in current ? data.segmentId !== current.segmentId : data.requestId !== current.requestId)) {
      return;
    }
    switch (event.type) {
      case 'answer.delta':
        setAnswer((s) => ({
          ...s,
          text: s.text + (data.token ?? ''),
          version: data.version ?? s.version,
          questionType: data.questionType ?? s.questionType,
          origin: data.origin ?? s.origin,
          sourceDocument: data.sourceDocument ?? s.sourceDocument,
        }));
        break;
      case 'answer.done':
        activeEvent.current = null;
        setCanCancel(false);
        if (answerStartedAt.current !== null) logAnswerTiming('display-complete', answerStartedAt.current);
        setAnswer((s) => ({ ...s, status: 'done' }));
        break;
      case 'answer.cancelled':
        activeEvent.current = null;
        setCanCancel(false);
        setAnswer((s) => s.status === 'streaming' ? { ...s, status: 'cancelled' } : s);
        break;
      case 'error':
        activeEvent.current = null;
        setCanCancel(false);
        setAnswer((s) => ({ ...s, status: 'error', error: data.message ?? 'The answer failed.' }));
        break;
    }
  }, []);

  return { answer, ask, cancel, canCancel, beginAuto, beginExternal, applyEvent };
}

function logAnswerTiming(stage: 'first-displayed' | 'display-complete', startedAt: number) {
  console.debug('[answer-timing]', { stage, elapsedMs: Math.round(performance.now() - startedAt) });
}
