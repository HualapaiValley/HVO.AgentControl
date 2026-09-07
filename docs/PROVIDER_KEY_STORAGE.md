# Managed provider API keys

Model access (`/providers`) lets the owner save an OpenCode Go API key and apply it to selected verified, connected runtimes. This complements runtime-owned ChatGPT OAuth; it does not clone refresh tokens.

The host stores the API key using the existing `Secrets.StoreEncrypted` vault. SQLite contains only an opaque secret reference, revision and timestamps. The credential file is encrypted with persisted ASP.NET Data Protection keys and restricted to its OS owner. Back up the data directory, including `keys` and `credentials`, together. A party with access to both the encryption keys and ciphertext can decrypt them; encryption does not replace host access control.

API reads return metadata only. Save and apply require owner authorization and CSRF validation. Keys are not included in events, model prompts, process arguments or provider error details. The form clears after submission; refresh does not reveal the saved value. Stale revisions cannot overwrite or apply a newer saved key. Replacing a saved key does not automatically rotate copies already installed on runtimes. Existing encrypted vault files are retained, consistent with other stored credentials; remote revocation and vault retention are separate follow-up work.

Apply uses OpenCode's authenticated `PUT /auth/opencode-go` over the existing verified SSH transport, with `{type:"api",key:...}` in the HTTP body. OpenCode owns the resulting runtime-local credential storage. No session disposal, server restart, prompt replay or model-default change occurs. The host records an unconfirmed receipt before sending; interruption or a lost response leaves that state visible after restart. Reapplying the same provider key is idempotent. A positive native response means **stored on runtime**, not verified model access or remaining subscription allowance. Refresh workspace models and run a small isolated task before admitting models to rotation.

Provider usage pools, cooldowns and bounded fallback are tracked in #82. Automatic key application during devcontainer provisioning remains part of #54; this first slice provides explicit owner-controlled reuse on connected runtimes.

Official setup: https://opencode.ai/docs/go/. Native endpoint contract: `docs/compatibility/opencode-1.18.29.openapi.json`.
