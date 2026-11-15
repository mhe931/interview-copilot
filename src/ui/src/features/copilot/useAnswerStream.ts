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
  status: 'idle' | 'streaming' | 'done' | 'error';
  error: string | null;
}

const IDLE: AnswerState = { question: '', text: '', questionType: null, version: 0, origin: null, sourceDocument: null, status: 'idle', error: null };

/** Streams answers for one session; a new question cancels the previous stream (atlas/copilot-pipeline.md). */
export function useAnswerStream(sessionId: string, fallbackProvider = 'ollama') {
  const { api, platform } = useApp();
  const [answer, setAnswer] = useState<AnswerState>(IDLE);
  const controller = useRef<AbortController | null>(null);
  const activeEvent = useRef<{ segmentId: string } | { requestId: string } | null>(null);
  const localRequests = useRef(new Set<string>());

  useEffect(() => () => {
    controller.current?.abort();
    activeEvent.current = null;
    localRequests.current.clear();
  }, [sessionId]);

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
      const abort = new AbortController();
      controller.current = abort;
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
            onDone: () => { if (!abort.signal.aborted) setAnswer((s) => ({ ...s, status: 'done' })); },
          },
          abort.signal,
        );
      } catch (err) {
        if (!abort.signal.aborted) {
          setAnswer((s) => ({ ...s, status: 'error', error: err instanceof Error ? err.message : 'The answer failed.' }));
        }
      }
    },
    [api, platform, sessionId, effectiveProvider],
  );

  /** Starts showing an automatic answer for a detected question (#22); a newer one replaces it. */
  const beginAuto = useCallback((question: string, segmentId: string) => {
    controller.current?.abort();
    activeEvent.current = { segmentId };
    setAnswer({ ...IDLE, question, status: 'streaming' });
  }, []);

  const beginExternal = useCallback((question: string, requestId: string) => {
    if (localRequests.current.has(requestId)) return;
    controller.current?.abort();
    activeEvent.current = { requestId };
    setAnswer({ ...IDLE, question, status: 'streaming' });
  }, []);

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
        setAnswer((s) => ({ ...s, status: 'done' }));
        break;
      case 'error':
        activeEvent.current = null;
        setAnswer((s) => ({ ...s, status: 'error', error: data.message ?? 'The answer failed.' }));
        break;
    }
  }, []);

  return { answer, ask, beginAuto, beginExternal, applyEvent };
}
