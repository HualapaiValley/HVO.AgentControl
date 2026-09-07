# Initial model routing research — 2026-09-07

This is a research baseline for #69 intake, #34 usage, #82 limits/fallback and #84 provider readiness. Recommendations below are proposed trial order, not a claim that benchmark winners have passed AgentControl acceptance. No live models were changed or extra model benchmarks launched for this research.

## Recommended starting policy

| Work | Initial choice | Next candidate to evaluate | Escalation |
| --- | --- | --- | --- |
| Coordinator routing, concise status, simple classification | Go MiMo V2.5 | GLM-5.3-Flash for ambiguous routing; Go Luna as a compatibility alternative | Strong assessment for unresolved high-risk decisions |
| Bounded implementation and routine fixes | Current Go MiniMax M2.7 / MiMo V2.5 baseline | MiniMax M3 first, then GLM-5.3-Flash | Sol or Astra when permitted and necessary |
| Independent review | A qualified model different from the implementer | GLM-5.3-Flash, then DeepSeek V4 Flash | Sol/Astra for credentials, migrations, concurrency and recovery |
| Large-context integration | Test M3 or MiMo before expanding context | MiMo V2.5 Pro, GLM-5.3, Kimi K3 for selected difficult cases | Astra for the hardest unresolved work |
| Deterministic chores | Code, CLI or telemetry query | Free MiMo for tasks that actually need language interpretation | Park rather than repeatedly ask a model to recover infrastructure |

These assignments describe task phases, not permanent worker specialties. Keep the explicitly requested Astra UI task parked during the ChatGPT budget hold. New candidates need an isolated canary before admission; the current live allowlist remains MiMo V2.5 and MiniMax M2.7 on Go.

## Go economics

Go costs $10/month. Published base limits are $12/5h, $30/week and $60/month, with model-dependent effective allowances. These are shared subscription constraints, not fresh balances per worker or model. Exact mixed-model debit accounting and remaining account allowance have not been verified. “Use balance” can spend Zen credit after Go exhaustion; its current account setting is unknown. [Go documentation](https://opencode.ai/docs/go/)

Published Go rates, USD per million tokens; “allowance” is the page's monthly usage equivalent, not a separate wallet:

| Model | Input | Output | Cache read | Allowance |
| --- | ---: | ---: | ---: | ---: |
| MiMo V2.5 | .14 | .28 | .0028 | 60 |
| MiniMax M2.7 | .30 | 1.20 | .06 | 60 |
| MiniMax M3 | .30 | 1.20 | .06 | 60 |
| GLM-5.3-Flash | .15 | .50 | .03 | 15 |
| MiMo V2.5 Pro | .435 | .87 | .003625 | 15 |
| DeepSeek V4 Flash, off-peak / peak | .22 / .44 | .66 / 1.32 | .007 / .014 | 30 |
| GLM-5.3 | 1.40 | 4.40 | .26 | 15 |
| Kimi K3 | 3.00 | 15.00 | .30 | 15 |
| GPT-5.6 Luna, ≤272K input | .20 | 1.20 | .02 | 15 |

M2.7 cache writes: $.375/M. DeepSeek peak: weekdays 01:00–04:00 and 06:00–10:00 UTC. Tables can change. [Go rate card](https://opencode.ai/docs/go/#usage-limits)

**Inference:** MiMo is a sensible baseline for repetitive coordinator traffic; M3 is the first general-worker upgrade to test. A stronger model can still reduce total cost by avoiding failed attempts, lengthy output and repeat reviews. Compare cost per accepted task, including repairs, rather than token prices or provider “requests per month” examples alone. Keep uncached input, cache reads, writes, output and reasoning accounting separate, avoiding double-counting reasoning already included in output.

## Capability evidence

All scores below are publisher-reported unless explicitly marked otherwise. They are evidence for choosing trials, not a common-harness ranking.

- **MiMo V2.5:** Xiaomi's card advertises up to 1M context and text/image/video/audio input capabilities. Its coding figure reports SWE-Bench Pro **56.1** and Terminal-Bench **2.0: 65.8**. This is a serious low-cost coding candidate as well as a coordinator option. The figure's “MiMo-V2-Pro” comparator is a different model; do not attribute its results to V2.5 Pro. [Publisher card](https://huggingface.co/XiaomiMiMo/MiMo-V2.5), [coding figure](https://huggingface.co/XiaomiMiMo/MiMo-V2.5/resolve/main/assets/mimo-v2.5-coding-bench.png)
- **MiniMax M2.7:** publisher reports SWE-Pro **56.22**, SWE Multilingual **76.5**, and Terminal Bench **2: 57.0**. It remains our verified operational baseline, but age alone is not a reason to keep it as the default. [Publisher card](https://huggingface.co/MiniMaxAI/MiniMax-M2.7)
- **MiniMax M3:** publisher reports SWE-Bench Pro **59.0** and Terminal-Bench **2.1: 66.0**, with native multimodality and 1M context. The larger context and same short-context price/allowance make this the first replacement candidate for M2.7. The version difference prevents directly treating 66 versus 57 as a measured relative gain in our harness. Its native reasoning modes include enabled/adaptive/disabled; our adapter exposes a different choice vocabulary. [Release evaluation](https://www.minimax.io/blog/minimax-m3), [publisher card](https://huggingface.co/MiniMaxAI/MiniMax-M3)
- **GLM-5.3-Flash:** publisher card and HF Terminal-Bench 2.1 submission report **84.3**. It is natively multimodal. Its documented reasoning efforts are low/high/max, and omission defaults to **max**. That makes it a strong coding/review canary, but a potentially wasteful default for short status requests unless reasoning is selected explicitly and actually supported by the adapter. [Publisher card](https://huggingface.co/zai-org/GLM-5.3-Flash)
- **DeepSeek V4 Flash-0731:** publisher reports Terminal-Bench **2.1: 82.7**, DeepSWE **54.4**, and Toolathlon-Verified **70.3**, using DeepSeek Harness and max reasoning for code-agent evaluations. Useful independent-review candidate. The Go alias is `deepseek-v4-flash`; verify which served revision it represents before transferring a dated model card's score to that endpoint. [Publisher card](https://huggingface.co/deepseek-ai/DeepSeek-V4-Flash-0731)
- **GLM-5.3 / Kimi K3:** publisher Terminal-Bench 2.1 reports are **88.2 / 88.3**. Their lower effective Go allowance makes them selective escalation candidates, not automatic everyday defaults. Kimi reports native visual capabilities and 1M context, but its best reported coding scores use its own Kimi Code harness. [GLM card](https://huggingface.co/zai-org/GLM-5.3), [Kimi card and harness notes](https://huggingface.co/moonshotai/Kimi-K3)

The [HF Terminal-Bench 2.1 leaderboard](https://huggingface.co/datasets/harborframework/terminal-bench-2.1) was checked through its public API as well as the page. The GLM-5.3, GLM-5.3-Flash and DeepSeek-V4-Flash-0731 rows all had **`verified: false`** and model-card sources at retrieval. Being on a Hugging Face leaderboard does not make a result independently reproduced. Preserve benchmark version, harness, reasoning effort, model revision, submission source and verification status. Do not mix Terminal-Bench 2.0/2.1/3.0, SWE Verified/Pro/Multilingual, or base-model and instruction-model scores. [HF leaderboard metadata contract](https://huggingface.co/docs/hub/leaderboard-data-guide)

Qwen3.8 Flash, Kimi K2.7 Code, MiMo V2.5 Pro and newer experimental offerings remain a second evaluation wave. Their presence in the catalog is not a reason to expand the initial pilot to every model. This first pass does not establish a universally best Go model.

## OpenAI and Zen are separate billing paths

Standard direct OpenAI API prices, USD per million tokens:

| Model | Input | Cached input | Output | Intended use in our policy |
| --- | ---: | ---: | ---: | --- |
| [GPT-5.6 Luna](https://developers.openai.com/api/docs/models/gpt-5.6-luna) | .20 | .02 | 1.20 | Cheap classification and focused tasks |
| [GPT-5.6 Terra](https://developers.openai.com/api/docs/models/gpt-5.6-terra) | 2.00 | .20 | 12.00 | Routine implementation needing stronger judgment |
| [GPT-5.6 Sol](https://developers.openai.com/api/docs/models/gpt-5.6-sol) | 4.00 | .40 | 20.00 | Integration and difficult reviews |
| [GPT-6 Astra](https://developers.openai.com/api/docs/models/gpt-6-astra) | 10.00 | 1.00 | 50.00 | Architecture, hard defects and critical boundaries |

Public model pages advertise 1.05M context and 128K output. Long requests above 272K input have different rates; do not cost the whole context at the base rate. Public API capabilities are not a guarantee of the same context allowance or tools through a subscription adapter. OpenAI's guidance also notes that stronger models can complete some tasks with fewer output tokens; actual end-to-end cost matters. [Current model guidance](https://developers.openai.com/api/docs/guides/latest-model)

Our `openai/*` workers authenticate through ChatGPT, so API prices are comparison data, **not their bill or remaining allowance**. Official Codex pricing describes shared local/cloud allowance, possible weekly limits, model-dependent consumption and a live usage dashboard; local/outside-GitHub reviews consume general usage. Published message estimates are not fixed quotas and have not been validated for OpenCode. Do not assume five separate device registrations provide five independent subscription budgets. [Subscription pricing and limits](https://learn.chatgpt.com/docs/pricing)

Zen separately lists Sol at **$2/$10** through **September 18, 2026**, a temporary 50% discount. Its DeepSeek rate card also differs from Go's. Store provider and billing route with every price. Zen supports workspace/member spend limits, but balance auto-reload is a separate payment control. [Zen pricing](https://opencode.ai/docs/zen/)

Zen's free MiMo, Big Pickle, Nemotron and Contributor offerings are trial/feedback options, not guaranteed permanent fallback capacity. Some have data-use restrictions or training/feedback terms; do not silently route arbitrary repository context to them. Start with owner-approved synthetic/public fixtures. Big Pickle's identity is opaque, so no exact open-weight benchmark can reliably be attached to it. [Zen availability and privacy](https://opencode.ai/docs/zen/#privacy)

## Observed integration facts and discrepancies

Read-only snapshot of our installed OpenCode 1.18.29 catalog and prior rollout evidence:

- Go MiMo V2.5 and M2.7 produced valid JSON-text responses on all five runtimes; one real MiMo coordinator decision dispatched two MiniMax tasks. This verifies integration, not engineering quality, robust schema compliance or an independent performance ranking. The five probes had different cache states; their timings are not a controlled latency comparison.
- Native JSON-schema mode has the separate #24 serialization problem. Do not blame model intelligence for that harness failure. Newly registered Go credentials needed an idle scoped instance refresh (#84) despite `connected` metadata.
- Our catalog advertises **204,800 context for M2.7**, **1,000,000 for M3/MiMo**, and **400,000 context / 272,000 input for ChatGPT-backed Sol/Terra/Luna**. Go Luna advertises 1,050,000 / 922,000. Apply the effective adapter limits, not the larger generic model-card number.
- Catalog GLM-5.3-Flash costs are **.075/.25**, while the published Go page says **.15/.50**. M2.7 cache-write metadata is zero although the Go page lists .375. M3 metadata adds a doubled-price tier above 512K that the compact Go table does not mention. These are unresolved discrepancies; use conservative estimates and flag them for reconciliation, not zero cost or an invented discount.
- ChatGPT model costs are zero in the native catalog. Record subscription usage and unknown financial cost, not “free.” Native reported costs need source and calculation-version labels.
- Go variants currently include M3 `none/thinking`, GLM/DeepSeek `low/high/max`, Kimi K3 `max`, and no named variant for MiMo/M2.7. Absence of a variant does not mean reasoning is disabled. Inspect the actual provider request semantics before exposing equivalent “reasoning levels.”

## Pilot and intake implementation

Build an isolated, repeatable pilot before automatic promotion. Start with current MiMo/M2.7 controls plus M3 and GLM-5.3-Flash; add DeepSeek after the first results. Use the same pinned OpenCode version, container image, repository revision, permissions, toolchain and task budget. Test cold and warm context separately. Resolve real provider access first without interpreting a tiny arithmetic probe as a coding score.

Use fixtures for: coordinator JSON/worker-ID discipline; a C# defect with hidden assertions; a Blazor layout with browser assertions; a seeded review with known severe findings; merge-conflict recovery; and long-running interruption after a recorded side effect. Include a migration/credential-boundary fixture for evaluation while keeping those production tasks at the existing strong-model floor. Repeat representative tasks, retain failures, and report sample sizes and uncertainty. Tiny pilots only establish candidacy.

Measure accepted task outcome, missed severe findings, false-positive review findings, tool-call correctness, human intervention, retries, output/schema failures, time to first visible progress, completion time, token/cache use and allowance consumption when authoritative. Count the full implementation → review → repair chain. Do not optimize for minimal output by suppressing useful progress.

Intake should first filter by owner override, effective capabilities, provider readiness, shared-pool health, budget and risk floor. Among eligible candidates, choose using task-specific observed success and expected total cost/time; require stronger assessment when uncertainty is high. Record the decision and policy version. Review independence matters more than making every reviewer cheap. Provider outages and missing CLI tools are infrastructure failures, not evidence to increase reasoning.

Research metadata should be separate from executable policy: provider/model/revision, billing route, retrieval date, rate-card expiry, context/output limits, adapter-supported modalities/variants, benchmark provenance, local evaluation count, and approval status. Refresh at least weekly and on catalog, pricing or provider-error changes; expire the Sol promotion explicitly. HF's documented leaderboard API can supply source/verification metadata for periodic review, but cannot grant runtime readiness or rewrite an owner's model choice.

Implementation links: #69 selection/risk floors, #34 usage ledger, #82 shared limits and safe continuation, #84 provider refresh. Automatic fallback must reconcile uncertain tool effects and remain inside the approved provider/model policy. The initial research does not enable paid overage, change current defaults, or lower the Astra requirement for the parked UI work.
