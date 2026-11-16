# Copilot: answer timeouts and microphone question repeats (#6)

**Requirements:** FR-07, FR-08, FR-09, FR-15; NFR-02, NFR-03, NFR-05, NFR-12

**Module:** Copilot, Audio, Transcription, Providers

**Spec file:** `specs/stories/copilot-03-answer-timeouts-microphone-questions.md`

## Story

As a person using InterviewCopilot during a live conversation, I want to know
why an answer is slow and explicitly repeat a question into my microphone, so
that I can get a useful answer without treating my ordinary speech as a
question.

## Acceptance criteria

```gherkin
Scenario: identify answer-pipeline delays
  Given a live spoken question
  When transcription and answer generation run
  Then safe timings distinguish transcription, question detection, context
       preparation, provider request, first token, first visible text, and
       completion without logging audio, transcript text, documents, or keys

Scenario: time out an answer by phase
  Given an answer request
  When its first token, an inter-token gap, or total run exceeds its own limit
  Then the UI shows the corresponding actionable error, keeps any partial text,
       and cancellation or a newer question does not become a timeout error

Scenario: explain provider failures
  Given a Gemini request fails
  When the provider returns an HTTP, quota, rate-limit, or stream error
  Then the UI explains the actionable category without exposing response bodies
       or secrets and does not retry indefinitely

Scenario: explicitly ask from the microphone
  Given a live session with existing microphone capture
  When the user activates Ask from mic and speaks one question
  Then only that deliberate candidate-voice segment is sent through the normal
       prepared-answer and generation flow, and the mode disarms

Scenario: ordinary speech is not a question
  Given Ask from mic is inactive
  When the candidate speaks
  Then no answer is generated from that speech

Scenario: cancel, retry, and follow up
  Given Ask from mic is active or an answer is streaming
  When the user cancels or submits a newer question
  Then capture intent is cleared, old work is cancelled, and stale output cannot
       replace the latest answer; a deliberate retry or changed follow-up works

Scenario: avoid a duplicate echo answer
  Given the same question is heard on system audio and the microphone
  When an answer for it is already active or complete
  Then a likely echo does not create an automatic duplicate, while an explicit
       user retry remains available
```

## Out of scope

- Enabling billing, changing the selected provider by default, or publishing a
  release or tag.
- Recording audio outside an explicitly active live session.
- Changing the existing Zoom/system-output capture path or storing audio.
- Treating microphone speech as an interviewer question when Ask from mic is
  inactive.

## ATLAS checklist

- [x] Architect: reuse the existing candidate-audio transcription path; keep
  default provider and capture behavior unchanged.
- [x] Trace: correlate stage durations with session/request identity only.
- [x] Link: use existing answer stream, prepared-answer lookup, cancellation,
  and session events; never put key or transcript content in diagnostics.
- [ ] Assemble: establish comparable baseline; add focused tests before code;
  implement backend timing/error behavior and explicit UI mic intent; rebuild
  and verify the installed Windows app.
- [ ] Stress-test: candidate speech outside the mode, interviewer/system audio,
  mic echo, repeat/retry/follow-up, rapid superseding questions, partial stream,
  provider failures, and session restart.

## GOTCHA

```
=== GOALS ===
Attribute answer latency, distinguish timeout phases, and let the user
deliberately submit one mic-transcribed question without treating ordinary
candidate speech as an interviewer question.
=== ORCHESTRATION ===
Baseline and key-safe model availability check; focused tests first; implement
timeouts/provider errors and mic-intent handling; run checks; rebuild, back up,
install and verify the user-local executable; review and merge only after gates.
=== TOOLS ===
Existing .NET 10, pnpm, Electron, local Whisper, Gemini BYOK model catalog,
and benchmark tooling. No new dependency or administrator access is required.
=== CONTEXT ===
FR-07..FR-09, FR-15; NFR-02, NFR-03, NFR-05, NFR-12; issue #6; existing
spoken-answer-latency and audio-capture-windows briefs.
=== HEURISTICS ===
Use existing capture tracks and prepared-answer reuse; keep short answers
incremental and context bounded; timeouts are phase-specific, bounded and
cancellable; never log personal content or secrets; do not enable billing,
change the provider default, or mask quota errors with retries.
=== ARGS ===
Gemini 3.5 Flash-Lite at minimal thinking; compare the currently selected model
with matched synthetic questions. Evaluate Gemini 3.8 Flash at low thinking
only if extra answer quality is useful. Preserve all user data and capture
behavior. Use current vendor-supported parameters only.
```

## What we adjusted

- Implemented the timeout/error/timing changes and explicit one-shot Ask from
  mic; added candidate gating, cancel/retry behavior, stale-answer protection,
  and prepared-answer timing in the benchmark output. Review fixes add safe
  handling for in-stream Gemini errors, Ollama request-header timing, and a
  Stop answer control for manual and automatic streams; automatic cancellation
  is scoped to the current segment and keeps partial text. No automatic
  provider change or continuous capture was added.
- Focused review-fix tests passed: backend unit (2/2), integration (1/1), and UI
  (2/2). Earlier checks passed backend unit (680/680), integration (106/106),
  architecture (4/4), and TypeScript coverage (348 tests; desktop 98.19%,
  extension 100%, UI 98.16%); lint, typecheck, UI/extension/desktop builds,
  API Release build, and self-contained Windows backend publish passed.
- The previous unsigned installer exists at
  `src/desktop/release/InterviewCopilot-0.1.0-windows-x64.exe`, but it predates
  the task commit and must be rebuilt before installation. The existing
  user-local app and its Start Menu shortcut remain intact; the old app was not
  running at the last inspection. Its prior build and user data were backed up
  under `%LOCALAPPDATA%\InterviewCopilot\backups\issue6-preinstall-20261116-125345`.
- Not verified: user-specific Gemini access or answer benchmarks, rebuilt-app
  startup, physical microphone/Zoom audio, echo, or restart persistence. Full
  solution build is blocked by existing Aspire vulnerability audit errors;
  backend coverage execution failed in the test reporter. No merge or branch
  cleanup has occurred; the unrelated local hook edit is preserved.
