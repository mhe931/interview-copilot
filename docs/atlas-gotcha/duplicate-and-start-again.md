# ATLAS + GOTCHA brief: Duplicate session and Start again

Status: owner instructed implementation on 2026-11-15; source implemented and
regression-tested. Installed-build verification is tracked separately below.

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
  Initialize legacy ended sessions from their valid start/end interval.
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

1. Use tracking issue #2 and its issue branch; the owner instructed completion
   using the agreed requirements on 2026-11-15.
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

The owner instructed completion using the agreed requirements on 2026-11-15.
Issue #2 remains open. The prior draft PR's planning-only description is not
evidence of installed-build updates or successful physical-audio tests.

## What we adjusted

- Reused the existing owned copy API rather than adding a duplication endpoint.
- Added one nullable accumulated-duration column. Legacy rows derive their
  duration from existing timestamps until the first restart initializes it;
  this avoids rewriting legacy timestamps or interpreting EF binary dates in SQL.
- Added a singleton per-session lifecycle gate and exclusive audio-stream lease.
  End terminates the lease, drains transcription, settles automatic answers,
  and clears per-run practice/key state before Start again can proceed.
- New audio connections offset their saved segments after prior history/active
  time; both Whisper and Deepgram follow the same session timeline.
- Added accessible editable-title duplication, restart controls, explicit
  Auto-answer state, stale-stream cleanup and capture-start generation guards.
- Regression checks: backend unit 662/662 and integration 102/102; UI 244/244
  with 98.78% line coverage; desktop 74/74 with 98.19% line coverage.
  UI coverage uses two workers to avoid local CPU contention and test timeouts.
- Backend unit coverage is 97.43% lines; architecture tests pass (4/4), the
  production API Release build has zero warnings/errors, and EF reports no
  pending model changes. The direct built test runner was used for coverage
  after the SDK's `dotnet test --no-build` failed internally.
- A read-only acceptance review found no Critical or Important source gaps;
  installed UI and physical audio remain independent verification steps.
- Backend unit compilation requires an explicit CA1859 exemption for the
  pre-existing Wave2ContractTests interface-contract declaration. No unrelated
  test or production analyzer configuration was changed.
- Portable packaging, actual UI verification, restart persistence and physical
  audio checks must be recorded after running the updated build; source tests
  and scripted audio integration tests do not establish microphone/Zoom success.
