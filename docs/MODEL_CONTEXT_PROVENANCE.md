# Model Context Provenance

The runtime model catalog stores a versioned `ModelCatalogObservation` in the additive `ModelCatalogObservations` table and exposes it through `GET /api/v1/runtimes/{id}/model-catalog`. It records the native adapter, directory, observation timestamp, catalog/schema versions, aggregate quality and per-model context, input and output limits. `Verified` means the pinned native `/provider` response contained all three non-negative limits; `Partial` or `Unknown` remains visible and is never converted into an invented budget.

`ModelChoice.Limits` carries the matching per-model observation alongside the existing provider/model selection data. This slice records evidence only. It does not reject prompts, select fallbacks, compact sessions, rotate control sessions or create a renewal hold.

Usage rows retain parent message identity, native summary attribution and finish reason. `EffectiveInputTokens` is present only when input, cache-read and cache-write counters are all present, and equals `input + cache.read + cache.write`. Missing cache counters therefore remain unknown rather than being treated as zero. This is context accounting, not quota or cost evidence.
