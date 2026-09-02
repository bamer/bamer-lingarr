# Changelog

## [2.13.0] - 2026-08-31

### Added
- **Progress bar for each translation task in data table** — Progress is now calculated from persisted `TranslationRequestLines` count in the database, so it's visible on page load and survives refreshes. No more relying solely on real-time SignalR events. Stored `TotalLines` on each request when translation starts.

### Fixed
- **Malformed JSON from translation models** — Models that output truncated or malformed JSON (missing `[`, extra `]`, truncated arrays) now get automatically repaired before parsing.

## [2.12.0] - 2026-08-31

### Fixed
- **Malformed JSON from translation models** — Models that output truncated or malformed JSON (missing `[`, extra `]`, truncated arrays) now get automatically repaired before parsing. Handles: missing opening bracket, missing closing bracket, truncated objects, extra trailing brackets.

## [2.11.0] - 2026-08-31

### Fixed
- **Failing tests** — Fixed `ProcessSubtitleBatch_MissingTranslation` to fallback to original lines while tracking `FailedPositions` (215/215 Server.Tests now pass) and updated `ProcessMedia_AllTargetLanguagesExist` to reflect stale-hash fix (hash not persisted when nothing to translate).

## [2.10.0] - 2026-08-31

### Fixed
- **LocalAI batch translation dying on transient backend errors** — A `503 ServiceUnavailable` (or timeout) from the local AI backend was converted to a non-retryable `TranslationException`, hit the catch-all, and killed the whole translation job with "All configured batch translation services failed" — no retries, nothing sent until manual resume. All request paths (structured output, JSON parsing, generate, chat) now throw `HttpRequestException` carrying the status code so the retry loop handles them with exponential backoff.
- **Retries now cover all transient failures** — Retry filter broadened from 429/503 only to 429, all 5xx (500/502/503/504) and transport-level errors (connection refused/reset). Request timeouts (`TaskCanceledException`) are retried instead of being treated as fatal. Permanent errors (400/401/...) still fail fast on the first attempt.
- **Structured output fallback hammering an overloaded backend** — A transient 503 on the structured-output attempt was swallowed and immediately replaced with a JSON-parsing request at the already-busy backend. Transient errors now bubble up to the retry loop; the fallback only kicks in for genuine "not supported" responses (4xx).
- **Empty model responses now retryable** — "No completion choices returned from LocalAI" is a model glitch and is retried (`TranslationParseException`) instead of aborting the job.
- **Settings save feedback inconsistent for text fields** — In the provider credentials form (Settings → Services), plain text fields like "AI Model" showed no green check icon and used a separate save branch, unlike validated fields (Address, numbers). All text/secret fields now use the same string validation (required fields must be non-empty), giving identical green-check feedback and save behavior. Verified via live test that changed LocalAI address/model apply to the next request without a restart.

## [2.9.0] - 2026-08-25

### Added
- **Per-page selector** — Select at the bottom of the translation panel to choose how many items are displayed per page (20/50/100/200).
- **Clear All button** — Removes every translation request in one click (with confirmation). Running jobs are cancelled first; lines and requests are deleted.

### Fixed
- **Caption semantics corrected** — Caption files (forced/SDH) now count as "language present" again: a lone `th.forced.srt` no longer triggers a full Thai re-translation, while still not blocking other missing target languages (regression from 2.8.0 filter).

## [2.8.0] - 2026-08-25

### Fixed
- **Automation blocked by caption files** — With `ignoreCaptions` enabled, one caption file (forced/SDH) for any target language skipped the whole media even when other target languages were missing (~2548 medias silently skipped). Caption files are now filtered upstream: they no longer count as existing translations nor block processing.
- **Automation stuck on zombie Hangfire lock** — `[DisableConcurrentExecution]` distributed lock survived ungraceful container restarts, blocking new runs for the full 10-min timeout ("Slow log ... OnPerforming"). Replaced with a local `SemaphoreSlim` gate: instant start when free, polite skip when already running.

## [2.7.0] - 2026-08-24

### Fixed
- **Automation enqueuing nothing** — Media hash was burned on failure paths (no valid source, captions, etc.) in `ProcessSubtitles`, permanently skipping 1477+ media with zero translation requests. `UpdateHash()` now only runs after requests are actually created, and the top-level hash check verifies active requests exist before skipping (self-heals stale hashes, no migration needed).
- **Logs freezing during heavy load** — Log stream detected new entries by queue count, but the bounded queue (1000) stops growing under flood → stream went silent while logging continued. Entries now carry a monotonic sequence number and the stream tracks a sequence watermark.
- **Log stream disconnects** — Removed manual `close()` + reconnect timer; the browser's native EventSource auto-reconnect handles drops properly.
- **Translation panel not updating live** — `RequestProgress` events for unknown request ids were ignored; the list now refreshes so automation-created requests appear as they start.

### Added
- **Header translation progress bar** — Active translation shows in the header: title (clickable, opens detail page), progress bar, percentage.

## [2.6.0] - 2026-08-16

### Added
- **Header translation progress** — Active translation now shows in the header bar with title (clickable), progress bar, and percentage.
- **Update detector fallback** — GitHub version check now falls back to tags if no releases exist.
- **Log stream reconnect timeout** — Increased to 90 minutes to handle slow local servers during heavy translations.

### Fixed
- **Duplicate lines on retry** — Failed lines were emitted before retry, causing duplicates in the output.
- **Batch translation fallback** — `UseBatchTranslation` setting now uses `TryGetValue` to prevent `KeyNotFoundException`.
- **MaxRetries/RetryDelay settings** — Now uses `TryGetValue` to prevent crashes when keys are missing from DB.
- **Column name mismatch** — `FailedPositionsString` now correctly maps to `failed_positions` column in MySQL.

## [2.5.0] - 2026-08-16

### Added
- **Partial translation status** — Batch translations with missing lines now auto-retry using existing retry settings (MaxRetries, RetryDelay). If lines still fail after retries, status is set to "Partial" with failed positions stored.
- **Failed positions tracking** — `TranslationRequest` entity now stores which line positions failed translation in `FailedPositionsString`.

### Changed
- **Update detector** — Now checks your GitHub repo (`bamer/bamer-lingarr`) instead of the original Lingarr API.
- **Batch translation flow** — `TranslateSubtitlesBatch` now returns `(subtitles, failedPositions)` tuple. Retry logic added per-batch with configurable attempts.

## [1.1.0] - 2026-08-08

### Fixed
- **SSE streaming parse error** — LocalAI responses starting with `data:` (Server-Sent Events) caused `JsonException: 'd' is an invalid start of a value`. Root cause: missing `stream: false` in chat requests. Fixed in all 3 chat methods and in `LocalAiChatTemplate` default.
- **Structured output fallback** — When `response_format: json_schema` is unsupported, the fallback now handles non-`ChatResponse` JSON (e.g. raw `{"translations":[...]}`) and strips markdown code fences.
- **JSON-only prompt enforcement** — Fallback path now appends explicit JSON format instructions to the system prompt.
- **Empty source language** — AI services now accept empty/unspecified source language for auto-detection instead of failing with "no service supports".
- **Endpoint URL normalization** — Base URLs like `http://localhost:8080/v1/` are now auto-completed to `http://localhost:8080/v1/chat/completions`.

### Added
- **Model Options panel** — New configurable parameters in the LocalAI plugin settings: Temperature (default 0.6), Top P, Max Tokens, Reasoning Budget, Chat Template Kwargs, Reasoning Effort. Empty values are not sent to the API.
- **Remove Completed button** — Bulk remove all completed translations from the list.
- **Retry Failed button** — Bulk retry all failed translations from the list.
- **Sortable table headers** — Click on Title, Status, or Completed column headers to sort. Shows ↑/↓ indicator for active sort.

## [1.0.0] - 2026-07-01

Initial release.
