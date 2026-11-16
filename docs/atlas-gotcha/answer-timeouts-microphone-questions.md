# Answer timeouts and microphone question repeats — #6

Status: **implementation authorized by the owner in issue #6**.

## Architect

Explain the slow-answer symptom with stage timings and meaningful timeout and
provider errors, and add an explicit, one-question microphone action for a
person who wants to repeat a question. Keep the existing capture pipeline,
prepared-answer reuse, answer stream, provider selection, privacy behavior, and
session history intact.

Out of scope: enabling billing, switching the provider default, continuous
background capture outside an active session, adding a second microphone
recorder, release/tag publication, and changing existing audio capture paths.

## Trace

1. Existing session capture sends interviewer/system audio and candidate/mic
   audio through separate, labelled tracks to the local backend.
2. The backend transcribes both tracks and publishes transcript-segment events;
   automatic answering continues to accept interviewer questions only.
3. While the user arms **Ask from mic**, the UI may consume one candidate segment
   as an intentional question. It calls the same answer endpoint used by typed
   questions, so prepared Q&A lookup, bounded grounded context, provider choice,
   incremental output, persistence, and cancellation remain shared.
4. The UI disarms after one accepted candidate segment or explicit cancel. The
   action is a normal keyboard-focusable control; it does not create a second
   stream or require a global shortcut.
5. Same-question echo must not silently overwrite an answer already in flight.
   Offer a deliberate retry when a candidate segment matches a recently
   answered system-audio question; changed follow-ups remain eligible.
6. Measure transcription, question detection, context selection/prepared lookup,
   provider request, first token, first visible text, and completion separately.
   Emit timings only; never include question, answer, audio, document text, API
   key, or provider response body.
7. Distinguish first-token, inter-token idle, total-answer, and caller
   cancellation paths. Keep visible partial text on a failure; bound retries
   (prefer no automatic retry) and report provider error categories accurately.
8. For Gemini, use only model-supported request fields. Keep existing
   `generateContent` streaming and selected provider; default to no model change.

## Link

| From | To | Method | Contract | Failure behavior |
|---|---|---|---|---|
| Existing audio capture | Transcription worker | labelled PCM frames | interviewer/system vs candidate/mic | preserve current independent tracks |
| Transcription worker | Live UI | `transcript.segment` | ID, speaker, offsets, text | only candidate segments while explicitly armed can initiate mic questions |
| Live UI | Answer API | existing SSE endpoint | normal question/request identity | cancel prior request; stale events ignored |
| Answer generator | prepared index / provider | existing application abstractions | prepared match first; bounded context otherwise | retain partial output; safe, phase-specific error |
| Gemini adapter | Gemini `streamGenerateContent` | SSE | supported model-specific generation settings | classify key, model, quota/rate, and service errors without body/key disclosure |

## Assemble

1. Establish build/tool availability, current settings and branch state; preserve
   the existing local hook edit and all user data.
2. Verify official Gemini model/API/price documentation; query the configured
   key's model catalog without printing or persisting the key.
3. Add backend tests for stage timing, timeout phase, partial stream,
   cancellation, Google error mapping, and model-specific request JSON.
4. Add UI tests for explicit one-shot mode, keyboard control, candidate speech
   gating, repeat/echo confirmation, cancel, stale answers, and session changes.
5. Implement the smallest production changes across the existing services.
6. Run comparable prepared-answer and full-generation benchmarks separately.
   Only make generation calls if the existing key is already authorized for
   that model and usage does not require changing billing; record samples,
   latency, error rate, token/cost estimate, and answer-quality rubric.
7. Run focused tests/lint/typechecks and documented repository checks, then
   build backend, UI, and Electron. Back up the prior executable and user data,
   install to the user-local location, and verify launcher and actual binary.
8. Update this brief with evidence and gaps; commit, push branch, open PR, wait
   for checks, and merge only if acceptance criteria pass. Sync main and clean
   up only merged branches.

## Stress-test

- First token never arrives; stream becomes idle after partial text; total
  deadline expires; cancellation and superseding request race.
- Gemini quota/rate limit, rejected key, unsupported model/parameter,
  malformed stream, and service outage.
- Mic speech outside Ask from mic; one deliberate question; same-text system/mic
  echo; retry same question; changed follow-up; rapid questions; stop/cancel.
- Prepared answer vs full generation timing; microphone and Zoom/system-output
  paths; restart persistence and existing Duplicate/Start again behavior.

## What we adjusted

- Implemented phase-specific first-token, idle-stream, and total timeouts;
  partial output retention; stage timing metadata; actionable Gemini error
  categories; model-specific thinking settings; separate prepared-answer
  lookup timing; and one-shot **Ask from mic** using the existing candidate
  capture path. Review fixes now map in-stream Gemini errors without exposing
  provider text, log Ollama response-header timing without prompt text, and
  provide a keyboard-accessible Stop answer control. Automatic-answer
  cancellation is scoped to the current segment and preserves partial output.
  The default provider and capture paths were not changed.
- Verified in this continuation: Gemini stream-error and Ollama timing unit
  regressions (2/2), automatic-answer cancellation integration (1/1), and UI
  cancellation tests (2/2). Earlier checks also passed backend unit tests
  `680/680`, integration tests `106/106`, architecture tests `4/4`;
  `pnpm lint`, `pnpm typecheck`, and `pnpm test:coverage` (33 files, 348
  tests; desktop 98.19% lines, extension 100%, UI 98.16%); UI, extension,
  desktop, API Release build, and self-contained `win-x64` backend publish.
- The previous unsigned installer at
  `src/desktop/release/InterviewCopilot-0.1.0-windows-x64.exe` was built from
  the uncommitted worktree based on `0b5cbfa62d676a714b398d593ab4f7d63ac9f94e`;
  it is not the final delivery package and must be rebuilt from the task
  commit. The old user-local install remains at
  `%LOCALAPPDATA%\Programs\InterviewCopilot\InterviewCopilot.exe`, with its
  Start Menu shortcut intact. The prior build and user data were backed up at
  `%LOCALAPPDATA%\InterviewCopilot\backups\issue6-preinstall-20261116-125345`.
- No Gemini model-access check, live benchmark, physical microphone,
  Zoom/system-audio, echo, or restart check was performed. Latency samples and
  model-specific quality/cost results are unavailable. The new candidate has
  not been installed or launched; the old app was not running and port 5480
  was not listening at the last inspection.
- Full solution validation remains blocked by NuGet audit errors for existing
  Aspire 13.6.4 packages (`NU1903`/`NU1904`); the backend coverage runner also
  exited with `KeyNotFoundException` before producing coverage. The formatter
  reported whitespace issues in an untouched integration-test file. These
  gates were not suppressed, and unrelated source/dependencies were not
  changed.
- No merge or branch deletion has occurred. The task branch and the unrelated
  `.claude/hooks/block-no-verify.sh` edit are preserved.
