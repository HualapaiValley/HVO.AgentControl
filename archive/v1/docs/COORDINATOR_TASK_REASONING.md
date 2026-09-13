# Per-task reasoning selection

Related: #69. This is the dispatch mechanism; durable intake classification, risk floors, budgets and escalation policy remain separate work.

A coordinator `send_prompt` action accepts optional `variant`, using the runtime's advertised OpenCode model variant names. For example, after verifying availability for the target runtime:

```json
{
  "type": "send_prompt",
  "workerId": "enrolled-worker-id",
  "text": "Design and implement the assigned change.",
  "providerId": "openai",
  "modelId": "gpt-6-astra",
  "variant": "xhigh"
}
```

Provider and model overrides must be supplied together. A variant without a model override uses the worker's default model. Omitting both model and variant inherits the worker defaults. An explicit empty variant resets reasoning to the provider default. Overriding the model without a variant also resets reasoning, preserving existing behavior rather than inheriting a setting that the replacement model might not support.

Before any batch action is dispatched, the service validates explicit model/reasoning selections against the current worker model catalog. Unsupported variants, unavailable models and reasoning settings on question replies reject the entire batch through existing bounded coordinator recovery. If the catalog is stale, refresh workspace inspection; the coordinator must not invent names or silently downgrade an explicit choice. The provider can still reject a previously advertised option; existing command failure handling applies.

The effective provider, model and variant are pinned in the durable command execution payload. Worker defaults and native session identity are unchanged, and host recovery does not recompute the selection. The existing OpenCode adapter forwards the pinned variant when submitting the prompt. Existing decisions without this optional field remain compatible.

Validation covers inherited, explicit, reset and replacement-model selections across a new store instance, unchanged defaults, and atomic rejection of invalid batches. No database migration or UI change is required.
