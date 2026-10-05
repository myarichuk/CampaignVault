# LLM cost counters and failure UX (Unity client)

Two features that share one seam: every model call goes through `OpenAiChatDriver.RequestReply`.

## Findings (current state)

- Tokens are already summed in `OpenAiChatDriver.SessionUsage` (`TokenUsage`, `TurnLedger.cs`) from the provider `usage` object. `stream_options.include_usage` is requested, with fallback. All presets (OpenAI, Anthropic compat, OpenRouter, Ollama, LM Studio, Custom) use this one OpenAI-compatible driver.
- `SessionUsage` is reset on conversation start and shown only as a text line in `DialogViewModels.cs:82` and the transcript export. No dollars, no persistence, no always-visible counter.
- DM turns already have a friendly transient message + RETRY (`FriendlyTransient`, `VaultController.cs:481`, `_lastSentLine`).
- Gaps for failures:
  - The raw provider text is a **static** `LastTechnicalError`, set only for transient errors and overwritten by the next one. Non-transient errors (400, 401, 404, parse failures) have no technical detail.
  - There is no copy button and no expandable detail.
  - Non-DM calls (`Brainstorm` callers: onboarding brainstorm/write-up, builder advisor/filler, party drafter, level advisor, `VaultController.cs:1269`, `.Builder.cs:730/792`, `.Party.cs:61/189`) each handle `error` strings ad hoc. None offers retry.

## Phase 1: Cost model

1. `ModelPricing` (new, `Scripts/AI/`): loads `Resources/VaultData/model-pricing.json` (bundled, data-driven). Rates per 1M tokens: `input`, `cachedInput`, `output`, keyed by model id with simple prefix/suffix matching (`anthropic/claude-sonnet-…` on OpenRouter, dated suffixes). Unknown model returns `null`, never a guess.
2. `ProviderProfile`: optional override fields `PriceInPerM`, `PriceCachedPerM`, `PriceOutPerM` (user wins over the table; also the only way to price Custom endpoints).
3. `TokenUsage`: add `Cost` (decimal/double) and `CostKnown`/`CostReported`. `FromJson` reads OpenRouter `usage.cost` when present, which is exact. Otherwise cost is computed from tokens × `ModelPricing` and flagged as an estimate. Local presets (Ollama/LM Studio) are flagged free. `Add` merges them, and any estimated or unknown component downgrades the total.
4. Display rules: exact `$0.42`, estimate `~$0.42`, unknown `tokens only · price unknown`, local `local · free`.
5. Tests (`DriverFakeProviderTests`, new `ModelPricingTests`): cost math with cached tokens, OpenRouter reported cost, prefix matching, unknown model, mixed exact/estimate, no usage reported.

## Phase 2: Two counters

1. **Session**: `SessionUsage` as now, with cost.
2. **Campaign (persisted)**: `CampaignUsageStore` writes `persistentDataPath/vault-usage-<campaignSlug>.json` (same pattern as `vault-providers.json`). It holds the running `TokenUsage` total, and optionally per-model totals so a model switch mid-campaign prices correctly. Updated after each call (flush on turn end, not per streamed chunk). Loaded on campaign open and switched on campaign change.
3. Hook point: wherever `SessionUsage.Add(usage)` happens (`OpenAiChatDriver.cs:315, 600, 652`), also feed the campaign store, so Brainstorm/advisor spend counts too, not just DM turns.
4. UI: a chip in the top bar (`TopBarViewModel`), e.g. `session ~$0.08 · campaign ~$1.92`. Tooltip or click opens a breakdown: in/out/cached tokens, calls, per-model, and exact-vs-estimate. A "reset campaign counter" action lives in Settings.
5. Tests: store round-trip, slug switch, estimate-flag propagation, corrupt/missing file falls back to zero.

## Phase 3: Unified failure model

1. `LlmFailure` (new): `Kind` (rate-limit, overloaded, auth, model-not-found, bad-request, network/timeout, malformed-reply, cancelled), `Friendly` (plain words, what to do), `Technical` (copy-pastable block: timestamp, endpoint host without key, model, HTTP status, request meta from `DescribeRequest`, attempt count, provider body truncated, and the key redacted), and `Retryable` flag.
2. `RequestReply` produces an `LlmFailure` for **every** failure path (not just transient). It replaces the static `LastTechnicalError` and `FriendlyTransient` string-mixing. `done(response, failure)` stays source-compatible by keeping the string `error` and adding `LastFailure` on the driver, so existing callers don't break, then migrating them.
3. Redaction test: no API key or auth header ever appears in `Technical` (the key lives in `vault-providers.json`, "never logged").

## Phase 4: Failure UI (expand, copy, retry)

1. Reusable `ErrorCard` control: friendly line, a "Details" foldout showing the technical text, a "Copy" button (clipboard), and "Retry" when `Retryable`. Reuse the existing ⚠ system-line styling.
2. DM turn: replace the plain ⚠ transcript line with the card, and keep RETRY wired to the existing `_lastSentLine` path. Retry must resend the **same** prompt and not duplicate the player line in the transcript.
3. Other call sites: onboarding brainstorm/write-up, builder advisor/filler, party drafter, level advisor each keep the last request (`messages`) so Retry re-runs the identical call. Auth/model-not-found failures offer "Open Settings" instead of Retry, using the existing `ProviderProblem` flag.
4. Inspect panel keeps showing the last technical error (now from `LastFailure`).
5. Tests: failure classification per status (0/200-dropped/401/404/429/5xx/400 with body), card state (retryable vs not), retry re-issues the identical request, no duplicate transcript line.

## Phases checklist

- [x] 1 Cost model + pricing table + tests (EditMode 305/305 green; PlayMode not yet run)
- [x] 2 Session + persisted campaign counters + top-bar chip + tests (EditMode 315/315, PlayMode 20/20 green; chip tooltip, Settings reset button and price override fields done)
- [x] 3 `LlmFailure` for all paths + redaction (EditMode 334/334, PlayMode green; `LlmFailure.cs`, `driver.LastFailure`, `LastTechnicalError` removed)
- [x] 4 ErrorCard + retry at call sites + tests (`ErrorCardViewModel`, `Common/ErrorCard.uxml`; DM turn, onboarding brainstorm/write-up, builder advisor/filler, party drafter, Inspect "LAST FAILURE"; the level advisor stays silent by design)
- [x] Full test suite green (EditMode 344/344, PlayMode 20/20)

## Open points

- Initial `model-pricing.json` values must be checked against each provider's current pricing page when written. They should not be typed from memory, and the file should carry an `asOf` date shown in the tooltip.
- The Anthropic compat endpoint may not report cache-write tokens, so estimates there can run slightly low with prompt caching. This is noted in the tooltip.
