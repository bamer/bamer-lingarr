# Changelog

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
