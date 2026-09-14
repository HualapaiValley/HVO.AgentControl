# CLIProxy Model Policy Lanes

**Catalog:** `cliproxy-phase1-2026-09-14-v1`  
**Control exposure profile:** `agentcontrol-control-phase1-v1`  
**Provider config:** `opencode-1.18.30-openai-compatible-v1`  
**Credential set:** `agentcontrol-system-phase1`  
**Source:** sanitized live CLIProxy configuration inspected 2026-09-14.

## Meaning and attribution

A `cliproxy/<id>` model is a client-visible policy/capability alias, not a
serving-model identity. The proxy uses weighted round robin across credentials,
one-hour session affinity and automatic failover. Force mapping may rewrite the
response model back to the alias. Therefore AgentControl records the configured
catalog/profile/config version and requested provider/model/variant, but does
not claim which source lane served a response unless the proxy later exposes
authoritative provenance.

Catalog curation limits what AgentControl asks OpenCode to expose; it is not hard
authorization at the proxy. The committed generated map intentionally excludes
full discovery and the image model.

**Independence is requested-lane independence only.** Two aliases may share a
DeepSeek (or other) fallback entry, so selecting two distinct aliases does not
prove two distinct serving models. The owner accepted recorded *requested policy
lane* evidence with that limit disclosed; do not describe it as true model
independence.

## Evaluated provider integration alternatives

The dashboard's generated OpenCode bundle defaults to
`opencode-cliproxyapi-sync@latest` plus an Oh-My plugin (`oh-my-openagent` or a
slim variant) and embeds an `apiKey` in the bundle. A lightweight community
plugin, `@projectmarc/opencode-cliproxy-provider` v0.2.0, supports
`CLIPROXY_BASE_URL`/`CLIPROXY_API_KEY`, cache/discovery and `/connect`.

Decision for the managed Phase 1 AgentControl runtime: **do not use dashboard
config sync, Oh-My plugins, MCP injection or dynamic discovery.** The committed
curated config remains the authority and the provider is a direct
`@ai-sdk/openai-compatible` block. Reasons:

| Alternative | Why it is rejected for managed authority |
| --- | --- |
| Dashboard config/bundle sync | Generated outside the reviewed repo, embeds the key in the bundle, and would silently reintroduce plugins/MCP not in the committed contract. |
| Oh-My-OpenAgent plugin set | Broad, mutable third-party behavior with its own tool/MCP surface; not reviewed here and not needed for a bounded control role. |
| MCP injection | Expands the managed employee's tool/authority surface; the control runtime deliberately injects no MCP servers. |
| Dynamic model discovery (`/connect`, cache) | The provider would resolve aliases at runtime instead of from the committed catalog, so the exposed set and fallback internals could drift without review. |
| Direct `@ai-sdk/openai-compatible` + committed config | Fully reviewable, no embedded key, deterministic model map, explicit variants, and identity confinement already bounds the child. |

The lightweight `@projectmarc/opencode-cliproxy-provider` plugin **may be
recommended only for general interactive OpenCode clients** on a workstation. It
is not the managed employee's authority, and no general interactive key is used
by AgentControl.

## Phase 1 catalog and sanitized ladders

The committed catalog keeps all 18 sanitized policy lanes for lookup and
documentation. A generated control/employee config exposes only the profile's
directly selectable subset.

| Alias | Advertised variants | Sanitized source-lane priority | Attribution class | Exposed |
| --- | --- | --- | --- | --- |
| `default` | low, medium, high | Claude Opus 5 and Codex Sol equal/top; DeepSeek Go; DeepSeek direct | mixed policy | yes (availability) |
| `free` | low, medium, high | Codex Luna; Luna Go; Muse; Nemotron; Big Pickle | free policy | yes |
| `deepseek-v4.1-flash` | low, medium, high | DeepSeek Go/direct; Luna; Qwen; Grok; GLM; Kimi; Muse; Nemotron; Big Pickle | review policy | yes (review pool) |
| `gpt-5.6-terra` | low, medium, high | DeepSeek Go/direct; Luna; Qwen; Grok; GLM; Kimi; Muse; Nemotron; Big Pickle | implementation policy | yes (workhorse) |
| `gpt-5.6-luna` | low, medium, high | Luna; Qwen; Grok; GLM; Kimi; Muse; Nemotron; Big Pickle | implementation policy | yes (cheap) |
| `qwen3.8-max` | none advertised | Qwen; Grok; GLM; Kimi; Muse; Nemotron; Big Pickle | fallback policy | no |
| `grok-4.6` | low, medium, high | Grok; GLM; Kimi; Muse; Nemotron; Big Pickle | fallback policy | no |
| `glm-5.3` | none advertised | GLM; Kimi; Muse; Nemotron; Big Pickle | fallback policy | no |
| `kimi-k3` | none advertised | Kimi; Muse; Nemotron; Big Pickle | fallback policy | no |
| `muse-spark-1.3-contributor-free` | low, medium, high | Muse; Nemotron; Big Pickle | free policy | no (fallback source) |
| `nemotron-3-ultra-free` | low, medium, high | Nemotron; Big Pickle | free policy | no (fallback source) |
| `big-pickle` | none advertised | Big Pickle only | anonymous policy | yes |
| `gpt-6-astra` | low, medium, high | Native Fable; DeepSeek Go/direct | high-risk review policy | yes (review pool) |
| `gpt-5.6-sol` | low, medium, high | Native Opus; DeepSeek Go/direct | review policy | yes (review pool) |
| `claude-fable-5.1` | low, medium, high | Native Fable; DeepSeek Go/direct | review policy | yes (review pool) |
| `claude-opus-5` | low, medium, high | DeepSeek Go/direct fallback entries; native Opus is exposed through Sol/default | review policy | yes (review pool) |
| `claude-sonnet-5` | low, medium, high | DeepSeek Go/direct; Big Pickle | focused correction policy | yes (correction) |
| `claude-opus-5-1m` | low, medium, high | Native Opus; DeepSeek Go/direct | high-context review policy | yes (review pool) |

The live inventory additionally advertised `auto`, native `claude-fable-5-1`,
`gemini-3.8-flash-high`, `gpt-image-2.5-sunburst` and others. They are not part
of the profile and are deliberately not generated. `big-pickle`, GLM, Kimi and
Qwen advertise no reasoning variants.

## Exact Phase 1 control exposure profile

The generated provider map exposes exactly these lanes, in this order:

```text
default, free, deepseek-v4.1-flash, gpt-5.6-luna, gpt-5.6-terra, big-pickle,
gpt-6-astra, gpt-5.6-sol, claude-fable-5.1, claude-opus-5, claude-sonnet-5,
claude-opus-5-1m
```

Qwen, Grok, GLM, Kimi, Muse and Nemotron exist only so an exposed lane can fail
over. They are never exposed and must never be selected as a task lane. `auto` is
never exposed.

### Task classes (generated agents)

Each task class becomes a step/tool-bounded, read-only OpenCode subagent with an
explicit model, variant and agent-level `options.reasoningEffort`; all tool
writes, shell and network access are denied and only sensitive-path-safe reads
are allowed. Both OpenCode AgentConfig step spellings (`steps` and `maxSteps`)
are generated with the same conservative value.

| Agent | Lane | Variant | Steps | External process timeout | Notes |
| --- | --- | --- | ---: | ---: | --- |
| `agentcontrol` (primary) | `Control:Model` (`cliproxy/default`) | `Control:ModelVariant` (`medium`) | n/a | host lifecycle | Consolidated manager/operations/IT control role. |
| `heavy` | `default` | medium | 64 | 30 minutes | Non-attributable availability lane; never an independent named review. |
| `workhorse` | `gpt-5.6-terra` | medium | 64 | 30 minutes | Primary workhorse implementation-policy lane. |
| `cheap` | `gpt-5.6-luna` | low | 32 | 15 minutes | Low-cost implementation-policy lane. |
| `correction` (focused correction) | `claude-sonnet-5` | medium | 32 | 15 minutes | Focused correction task lane. |

No review agent is generated. An independent review must **explicitly** select
one of `gpt-6-astra`, `gpt-5.6-sol`, `claude-fable-5.1`, `claude-opus-5`,
`deepseek-v4.1-flash` or `claude-opus-5-1m`; `default`, `free`, `big-pickle` and
`auto` are never a review draw. Every review starts a fresh OpenCode session;
compaction within that session does not authorize continuation after the step
limit. Step exhaustion ends the attempt with no automatic continuation. The
external runner must cancel and terminate the process tree at its declared
15/30-minute policy deadline and record an incomplete review rather than treating
a timeout as success.

OpenCode 1.18.30 exposes no wire-verified per-agent context or output-token cap in
this profile. Catalog context/output limits are currently `0` (unknown), so this
contract is deliberately described as step/tool-bounded, not context/output-
bounded. Concrete context/output enforcement remains unavailable until an
authoritative provider limit or a real-binary-verified OpenCode option exists.

### Variants

OpenCode 1.18.30's published config schema is permissive around provider model
options and does not by itself prove the outbound request shape. The pinned
outbound integration test therefore runs the real binary against a disposable
local OpenAI-compatible endpoint. It verifies that model variant entries carrying
`reasoningEffort` produce the matching wire-level `reasoning_effort`, for example:

```json
"variants": { "medium": { "reasoningEffort": "medium" } }
```

An empty option object would request a variant with no effort and let the
provider silently choose a default, so every advertised variant states its own
value. OpenCode 1.18.30 additionally requires the effective lane's model-level
`options.reasoningEffort` to be populated; the variant entry and agent-level
option alone produced wire-level `low` in the real-binary direct-agent test. The
generated config therefore repeats each task class effort at model level, and
the outbound test generates the actual Compose primary profile
(`cliproxy/default` medium), invokes `--agent workhorse`, and locks Terra medium
to `reasoning_effort: medium`. Big Pickle advertises no variants and is generated
with an empty map.

## Configuration and secrets

A CLIProxy-required runtime must configure all of:

- `Control:Model=cliproxy/<selectable-alias>`;
- `Control:ModelVariant=medium` where the lane advertises it;
- an absolute HTTP(S) `Control:CliProxyEndpoint` whose path is exactly `/v1`
  (no query or fragment), so authenticated catalog validation always targets
  exactly `/v1/models`;
- an absolute `Control:CliProxySecretFile` containing at least 16 bytes with no
  whitespace or control characters. One final LF or CRLF is framing and removed;
  no other leading/trailing content is trimmed.

The controller reads the file immediately before process start and exports
`CLIPROXY_API_KEY` only to the OpenCode child. tmux and the terminal PTY bridge
never receive it, and the privileged launcher forwards the key only for the
`acp` operation. The key is not put in model config, command arguments, the
database or logs. The generated direct OpenAI-compatible provider references
`{env:CLIPROXY_API_KEY}` and explicitly maps only the committed profile.

This is process-environment containment, not protection from every same-UID
child. OpenCode tools and other processes running as the agent UID may inspect a
sibling/ancestor environment where the OS permits it. Phase 1 accepts that
limitation for one inference-only shared AgentControl system key; it does not
make the key suitable for broader authority.

Before OpenCode starts, the controller performs a bounded authenticated
`GET /v1/models` and does not send an inference request. A 401/403 records the
sanitized provider status `revoked`; network errors, timeouts, 5xx, wrong content,
malformed/empty JSON, a missing exact selected lane ID, and responses over 1 MiB
record `unavailable`. Only a bounded valid OpenAI-compatible model list containing
the exact selected lane records `configured`. Response bodies are parsed but
never logged, and no key or fingerprint is persisted. If
endpoint, catalog lane, variant, secret or preflight validation fails, a runtime
that selected `cliproxy/*` faults before OpenCode starts. It never substitutes
`opencode/big-pickle`. Big Pickle is permitted only when explicitly selected as
the anonymous capability.

The checked-in HTTP endpoint is an owner-authorized trusted-LAN deployment
constraint. Plain HTTP must never be routed over the Internet or any untrusted
network; use HTTPS before crossing such a boundary.

## Provisioning and rotation

The named **AgentControl** dashboard key is provisioned from stdin into the
external secrets volume. It is never passed as an argument or environment value,
never printed, and no fingerprint is stored. The provisioner validates length
(>= 16 non-control characters), rejects symlinks, non-regular files, extra hard
links and unexpected owners, repairs UID/GID/mode metadata on identical input
through the same inode and verifies it, and atomically replaces on change. A
failure before rename reports that publication did not occur; any directory
fsync or verification failure after rename exits distinctly and reports
`publication may have occurred; reconcile file before retry` rather than claiming
the prior file is unchanged or a retry is automatically safe.

Exact command shape (the operator supplies the named AgentControl key; no
database password or raw query belongs in this repository). The key goes in on
stdin from a protected source, never as an argument or environment value:

```bash
# Stop every CLIProxy runtime sharing credential set agentcontrol-system-phase1,
# then pipe the named AgentControl key straight into the provisioner:
scripts/init-secrets.py --provision-cliproxy-key < /secure/path/agentcontrol-cliproxy-key
```

The dashboard key is not used. Do not substitute the general interactive
OpenCode/workstation `CLIPROXY_API_KEY`.

The provider plugin is useful only for general interactive OpenCode clients.
AgentControl does not use dashboard sync: that path embeds an API key and enables
a broad plugin surface that is outside this runtime's policy. The general
interactive OpenCode key remains separate from the one global AgentControl
inference key shared by the host and managed employees.

There are no Finance records or Finance runtime now. Future Finance usage
reporting must correlate proxy usage with AgentControl-persisted employee,
runtime, session, task and request IDs. Shared-key totals alone cannot attribute
usage to Finance or to any employee.

Rotation workflow (deployment-wide; the same credential set is shared):

1. Hold dispatch and reconcile active work.
2. Stop the controller and every worker/runtime that uses
   `agentcontrol-system-phase1`.
3. Provision the new key from stdin with `--provision-cliproxy-key`. Rotation
   refuses while the named control container is running.
4. Restart and revalidate each runtime (new session, requested lane and variant
   observed) before clearing the hold.

Revocation has the same collateral: it makes every CLIProxy-required runtime
using the credential set unavailable; explicitly anonymous Big Pickle runtimes
are unaffected. Provider rejection after process start is a provider/turn
failure and must not be reported as a successful model selection.

## Activation status

The code is ready. Deployment is pending a reviewed merge plus provisioning of
the named AgentControl key. Compose selects `cliproxy/default` at medium against
`http://home-docker.home.lan:8317/v1`, so after merge a missing key fails closed
by design until the operator runs the provisioning step. General interactive
OpenCode clients keep their own separate key.
