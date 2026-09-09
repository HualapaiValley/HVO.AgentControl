# Task risk floors

Every new worker prompt and coordinator `send_prompt` action declares one risk level: `low`, `medium`, `high`, or `critical`.
The service resolves the exact inherited or overridden provider/model before recording the command and compares the task risk
with `Control:TaskRiskFloor`. A route can handle its configured maximum risk and every lower level. Unclassified routes are
limited to `low` by default.

The built-in `risk-floor-v1` policy classifies OpenAI Luna as low, Terra as medium, Sol as high, and GPT-6 Astra as critical.
Deployments can replace or extend `RouteMaximums` through normal ASP.NET configuration. Keys are exact
`providerId/modelId` pairs; model names in task prose never change the route or its floor.

Requests below the configured route floor are rejected before durable acceptance with HTTP 409 and code
`risk_floor_not_met`. Missing or invalid risk levels are rejected with `invalid_request`, `risk_level_required`, or
`risk_level_invalid` as applicable. Error details include the requested risk, exact provider/model, configured route maximum,
and policy version. Accepted command payloads freeze that policy version and route maximum alongside the exact route. The
supervisor repeats the check at claim and immediately before native mutation, including matching the frozen admission
provenance to the active policy. A stored command that
does not pass is marked failed with the same structured result and a `TaskRiskFloorRejected` event; it is never silently
rerouted or replayed.

When native assistant evidence identifies a provider/model different from the frozen admitted route, the supervisor records a
`TaskRiskRouteMismatch` event and fails the task outcome after settlement. It preserves the native result for review and does
not replay the prompt.
