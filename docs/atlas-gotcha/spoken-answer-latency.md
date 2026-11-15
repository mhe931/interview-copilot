# Spoken-answer latency — #4 (ATLAS + GOTCHA)

Status: **approved by owner on 2026-11-16**.

## Architect

Improve the time from the end of a spoken question to a useful visible answer,
without changing the selected provider, introducing a paid default, weakening
grounding, or mixing microphone and meeting audio. Support the existing Short
answer and Longer controls, and avoid imposing STAR on questions for which it
is inappropriate.

Out of scope: hosted infrastructure, new mandatory services, provider changes,
release publishing, and changes to existing personal data or credentials.

## Trace

Measure speech end, transcript finalization, question detection, context
preparation, provider request, first token, first visible text, and completion.
Compare typed and spoken inputs using the same questions and configuration,
and report cold and warm samples separately. Use aggregate timings only: never
log questions, answers, document content, audio, or credentials.

The current code already extracts document text on upload, applies a
6,000-character document-excerpt budget, and streams provider tokens. These
are starting points, not proof that the installed app is fast or streams
correctly. The transcription worker processes audio frames sequentially while
each segment is transcribed; the default voice-activity pause is 600 ms.
Neither observation establishes the dominant real-world bottleneck.

## Link

- Audio capture and transcription remain separated by speaker; changes to
  endpointing or inference require measured evidence and accuracy checks.
- Prepared document passages remain local; any provider receives only the
  information authorized by the existing provider selection.
- Generated and prepared answers must be distinguishable, grounded, cancellable,
  and safe across session restart and rapid questions.
- Main window and overlay must receive incremental updates; history and answer
  controls must retain their existing behavior.

## Assemble

1. Establish a safe baseline with the installed configuration; record hardware,
   model and context sizes without recording personal content.
2. With tests first, address the measured bottlenecks in the existing pipeline:
   reuse already-extracted documents, cache or index retrieval work outside the
   answer path, bound context, and invalidate derived data on relevant changes.
3. Index recognizable prepared question-and-answer material before a meeting.
   Reuse only when a validated match is sufficiently reliable; otherwise use the
   grounded generation path. Matching and fallback require negative tests.
4. Verify incremental delivery, cancellation, failure handling and answer
   identity across both views and successive questions.
5. Rebuild the backend, UI and desktop using the supported packaging flow,
   preserving the previous executable and all user data. Verify the executable
   and shortcut and repeat comparable measurements.

The matching method and confidence threshold, instrumentation boundaries, and
any transcription adjustment depend on baseline measurements and test cases.
Do not silently change the selected model or provider. Any change to model,
cost, privacy, or capture behavior requires explicit user choice.

## Stress-test

- Measure P50 and P95, sample counts, failures, cold/warm runs, and answer
  quality separately for synthetic, typed, microphone and remote meeting audio.
- Check prepared-answer false positives, fallback, document and settings
  invalidation, grounding, and the context budget.
- Check overlapping questions, cancellation, provider failures, Duplicate,
  Start again, history and restart persistence.
- Run focused backend and TypeScript tests, applicable lint and typechecks,
  and exercise both main and overlay views in the rebuilt Windows executable.

## GOTCHA

- **Goals:** improve measured time to first useful visible text while retaining
  accuracy, privacy and working session history.
- **Orchestration:** baseline, test-first measured changes, regression checks,
  complete rebuild, matched verification, review and delivery.
- **Tools:** existing .NET, pnpm, Electron, local Whisper and the user's
  already-selected provider; no Ollama or elevation required by this work.
- **Context:** repository constitution and functional requirements FR-07 to
  FR-10 and FR-13; non-functional requirements NFR-02 and NFR-05.
- **Heuristics:** preserve unrelated work and personal data, no silent cloud
  changes, no personal content in logs or Git, and no unsupported speed claims.
- **Args:** actual configured settings and matched benchmark inputs; keep
  private inputs and measurements out of public artifacts.

## What we adjusted

- Document text was already extracted at upload and retrieved within a bounded
  budget. We added an in-memory index for explicitly labelled, exact-match
  question-and-answer pairs rather than replacing that retrieval path.
- Prepared answers are identified with their document source; edits to the
  answer still use the configured provider. Ambiguous matches fall back to
  grounded generation.
- Answer events carry question or request identity so both windows can stream
  updates and discard stale events. STAR guidance is conditional on a relevant
  past example.
- Synthetic indexing and retrieval timings are not end-to-end latency evidence.
  Comparable installed-app, microphone and meeting-audio measurements remain
  necessary before claiming a real-world speedup.
