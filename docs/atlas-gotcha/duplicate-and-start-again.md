# ATLAS + GOTCHA brief: Duplicate session and Start again

Status: tracking established; awaiting owner approval before implementation.

Tracking: [mhe931/interview-copilot#2](https://github.com/mhe931/interview-copilot/issues/2).
Branch: `issue/2-duplicate-start-again`.
Milestone: `v0.3 Supporting`.

## Goal and boundaries

Add Duplicate to list/detail actions and Start again to ended sessions.
Preserve credentials, original sessions/documents, all saved history, and
unrelated work. No Ollama, elevation, reinstall, or new dependencies.
Microphone verification requires physical input; Zoom remote audio is a
separate acceptance check and must not be inferred from typed questions.

## ATLAS

### Architect

- Reuse `POST /api/sessions` with `copyFromSessionId`; it already checks
  ownership, copies settings including AutoAnswer and document content,
  and creates a Draft with new session/document IDs and no history.
- A title-entry action supplies an editable duplicate title using existing
  form/dialog controls. Duplication never calls Start or capture.
- Reuse `POST /api/sessions/{id}/start` and the desktop's normal start/key
  hand-off/capture/overlay workflow for Draft and Ended sessions.
- Preserve AutoAnswer exactly; show its state on session actions/details.
- Persist accumulated active recording time separately from wall-clock
  StartedAt/EndedAt. Existing timestamps alone cannot preserve multiple
  active intervals without counting stopped time or falsifying timestamps.
  Proposed minimal schema addition: one accumulated-duration column.
  Backfill existing ended sessions from their valid start/end interval.
  No table for individual runs is proposed.
- Retain original CreatedAt and history on restart; clear EndedAt while
  Live and use the current run's StartedAt. New transcript offsets append
  after prior recorded time, never restart at zero in the saved timeline.

### Trace

1. Duplicate: enter title, submit once, existing owned-copy API, navigate
   to new Draft; original unchanged, capture not started.
2. Start again: reject concurrent lifecycle transitions, settle the old
   audio/answer work, restart the same ID, hand off keys, create exactly
   one capture window and show overlay.
3. End: stop capture and drain/settle final transcription, cancel pending
   automatic answers, clear per-run provider/practice state and keys,
   save active duration and EndedAt. Never delete transcript/answer rows.
4. Repeated starts/ends must not leave overlapping workers, sockets,
   subscriptions, capture windows, or late writes from a previous run.

### Link

- Domain: lifecycle guards, active-time accumulation.
- Application: owned duplication (existing), serialized lifecycle cleanup,
  transcription offset/drain, automatic-answer cancellation.
- API: keep existing owned session routes; conflict errors are explicit.
- UI: existing ApiClient/platform bridge, native buttons, title dialog,
  disabled/loading controls, accessible error/status announcements.
- Desktop: serialize capture startup and make repeated start requests
  unable to replace a newly created active window accidentally.
- Persistence: one migration only if the approved duration design needs
  it; test legacy data/backfill before updating the user's installation.

### Assemble

1. Use tracking issue #2 and its issue branch; obtain brief approval.
2. Write focused failing domain/application/API and UI/desktop tests.
3. Implement lifecycle/duration/history safety first, then reusable
   Duplicate action and Start again using existing start workflow.
4. Update directly related README/lifecycle documentation.
5. Run targeted tests, UI/desktop typecheck/lint, backend Release checks;
   inspect diff/status before building.
6. Build/publish with the existing user-local Node, pnpm, .NET toolchain.
   Update the actual portable app only after a graceful stop; preserve
   user data and credentials and retain a recoverable prior build.

### Stress-test

- Owned ended session duplicated with all reusable settings/documents;
  new ID, new document IDs, empty history, original remains unchanged.
- Foreign/nonexistent source and foreign restart follow existing 404
  ownership behavior.
- Start/End/Start on the same ID preserves earlier transcripts/answers.
- Stopped intervals excluded from accumulated recording duration;
  appended offsets are monotonic across runs and reconnects.
- Rapid repeated UI actions and concurrent API starts cannot create
  duplicate capture/transcription/answer work.
- Stop works on every cycle; failures show useful retryable errors.
- Saved AutoAnswer remains off when originally off, never silently
  enabled by Duplicate or Start again.
- Desktop restart preserves configuration and appended history.
- Real mic transcription/automatic answer tested when the owner can
  supply physical input. Remote Zoom speech tested separately.

## GOTCHA

- Goals: user's Duplicate and Start again acceptance criteria above.
- Orchestration: tests, backend lifecycle, UI/desktop, validation, install.
- Tools: existing pnpm/.NET/Vitest/xUnit/EF tooling, no new dependencies.
- Context: CLAUDE.md, GOTCHA-PROJECT.md, docs/ai-workflow.md; current
  copyFromSessionId prior art in SessionService.
- Heuristics: preserve ownership checks and keychain boundaries; never
  log credentials, audio, transcripts, or documents; never bypass policy.
- Arguments: existing API/desktop ports and provider preferences unchanged.

## Guidance and blockers

Modern Web Guidance search/retrieve succeeded with DISABLE_TELEMETRY=1
and generic queries. Consulted accessibility and
accessible-error-announcement; use relevant native controls, names,
focus, and stable live announcements without unrelated form rewrites.

GitHub Issues are enabled. Issue #2 has the required layer, type, priority,
and module labels and the `v0.3 Supporting` milestone. The owner authorized
issue/metadata creation, an issue branch, commit, feature-branch push, and
a draft PR. No merge, tag, or repository-settings change is authorized.

Owner approval of this brief is required before product code is written.
The draft PR contains planning documentation only; it is not evidence of
implemented features, installed-build updates, or successful audio tests.

## What we adjusted

Not implemented yet.
