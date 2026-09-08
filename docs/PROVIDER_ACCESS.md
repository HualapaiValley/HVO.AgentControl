# Runtime provider access

## Current implementation

The **Models** page (`/providers`) starts ChatGPT subscription device sign-in on a verified, connected runtime. It uses OpenCode 1.18.29's `/provider/auth`, `/provider/openai/oauth/authorize`, and `/provider/openai/oauth/callback` through a separately authenticated SSH transport. It discovers the headless method by label, rather than assuming a numeric method index. Only the official OpenAI device URL and a validated one-time code are presented to the owner.

AgentHost holds public progress receipts in memory, with one pending attempt per runtime and at most four concurrent attempts. Navigation or refresh does not stop a pending attempt. A host restart loses pending tracking; the owner must check runtime authentication before retrying. A ten-minute local deadline bounds tracking, not the provider code's lifetime. A lost callback response is **Unknown**, because closing the local connection cannot prove the remote OAuth exchange stopped. Event history contains request ID and state, never device codes, access tokens or refresh tokens.

OpenCode stores and renews credentials on the runtime under its OS user. Workers using that same user share provider access, while model selection remains a worker setting. Signing in does not abort sessions, clear native instances, change model defaults, retry failed tasks or copy credentials to other hosts.

After sign-in, refresh workspace models in the worker editor. Existing OpenCode workspace instances can retain provider configuration loaded before authentication; a catalog refresh alone may not reload the provider plugin. If a newly selected provider cannot produce a response, reconcile active tasks before a controlled workspace/server reload. The saved Go-key flow now supports a controlled, idle-only cache refresh with durable holds and process continuity; see [Provider refresh completion](PROVIDER_REFRESH_CONTINUITY.md). OAuth refresh integration remains follow-up work. Authentication success does not claim an inference test passed.

## Provisioning contract (planned)

Provider setup belongs to the runtime provisioning record, both at creation and afterward. Issue #43 should offer an optional **Model access** step after the devcontainer is built and verified:

1. Select reusable provider profiles, or choose **Configure later**. Profiles describe the provider and credential-delivery policy; they are not credentials baked into an image.
2. Install/verify the pinned OpenCode integration and discover supported sign-in methods.
3. Configure access, or show **Waiting for provider sign-in** with resumable owner instructions. Runtime readiness and model-access readiness are separate. A runtime can still provide a terminal while provider setup is incomplete.
4. Verify the selected model with a bounded, tool-free inference probe before allowing assignments that require it.
5. Assign workers from the verified model catalog. Additional providers can be configured later without recreating the runtime.

Initial profile targets are ChatGPT/Codex subscription, OpenCode Go/Zen, GitHub Copilot, API-key providers and local endpoints. Only ChatGPT's device flow is implemented here. Each other integration needs its supported authorization flow, account requirements, credential storage and renewal verified independently; GitHub model access is separate from the GitHub App repository credential grant. Multiple profiles may be attached to one runtime.

Profiles must never carry secrets in devcontainer.json, image layers, build arguments, repository files, prompts or browser responses. devcontainer.json may reference a non-secret profile ID. Deliver runtime credentials after creation over the verified control channel, using restricted files or a broker.

## Credential reuse for managed devcontainers (planned)

Do not distribute one rotating OAuth refresh token to several independently refreshing OpenCode processes. In the pinned OpenCode implementation, refresh is serialized by a promise inside a single provider instance, not across containers. Each process can write replacement credentials independently. Our current five ChatGPT registrations are independent authorizations, not copied token files.

The preferred managed design is one credential owner per subscription profile, with serialized refresh and explicit revocation. Evaluate a provider-compatible inference gateway so short-lived container identities authorize calls while the gateway owns the subscription credential. If credential delivery is used instead, establish a supported non-refreshing consumer contract and synchronized updates before sharing a refresh lineage. Compatibility with provider subscription endpoints and terms needs verification before implementation.

On teardown, revoke the container's access to the profile and remove its delivered secrets. Rebuilding a cached image does not confer account access. Track provider readiness, refresh failures and inference availability separately from host health.

## References

- [OpenCode 1.18.29 provider authentication](https://github.com/anomalyco/opencode/blob/v1.18.29/packages/opencode/src/provider/auth.ts)
- [OpenCode 1.18.29 ChatGPT OAuth and refresh implementation](https://github.com/anomalyco/opencode/blob/v1.18.29/packages/opencode/src/plugin/openai/codex.ts)
- [OpenAI headless sign-in](https://learn.chatgpt.com/docs/auth)
- Tracking: #54 provider access; #43 devcontainer provisioning.
