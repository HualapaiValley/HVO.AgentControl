# Worker GitHub access

The `/github` owner page configures GitHub App access for an execution runtime. AgentControl stores the App private key encrypted and issues installation tokens restricted to the selected repositories and Contents/Issues/Pull requests write permissions. It verifies the installation account, returned repository set, permission set and expiry before delivery. The private key never goes to a worker.

## Initial setup

1. Open [Register a GitHub App](https://github.com/settings/apps/new) while signed into the repository owner's account. Use a unique name such as `HVO-AgentControl-RoySalisbury` and the AgentControl repository URL as the homepage. No OAuth callback is needed for this installation-token integration. Disable the webhook; select installation only on your account.
2. Under repository permissions set **Contents**, **Issues** and **Pull requests** to **Read and write**. Leave other permissions at their defaults. Metadata read access is implicit. This does not request administration, organization management or workflow modification permissions.
3. Create the App, note its numeric **App ID** from its settings page, and generate/download its PEM private key. The alphanumeric **Client ID** is not used by AgentControl.
4. In the App's settings sidebar choose **Install App**, then **Install** beside your account. Choose **Only select repositories**, initially only `RoySalisbury/HVO.AgentControl`, and install. Creating the App alone does not create an installation.
5. Open [Installed GitHub Apps](https://github.com/settings/installations) and choose **Configure** beside the App. The final number in the URL is the numeric **Installation ID**: for example, `https://github.com/settings/installations/12345678` means installation ID `12345678`. Organization installations use `https://github.com/organizations/ORG/settings/installations/12345678` instead.
6. In AgentControl's **GitHub** page select **Devcontainer .NET test**, enter the App ID, installation ID and `RoySalisbury/HVO.AgentControl`, and paste the PEM into the private-key field. Submit **Verify and configure access**. The field is cleared after submission. Never put the key in an agent prompt or chat.
7. Keep the runtime connected. The background service delivers credentials over its pinned SSH connection and shows **Ready** or a visible **Blocked** reason. Existing personal GitHub configuration is not overwritten. All sessions using this runtime's OS account share the grant.

The owner must register/install the App in GitHub; possessing a repository deploy key or the host's existing `gh` login does not create an App installation automatically.

## Worker use and renewal

Credentials are written atomically to the runtime account's GitHub CLI `hosts.yml` with mode 600, in a protected canonical configuration directory. `gh` can then read issues, create PRs, publish reviews and comments within the selected repository permissions. No credential is passed in a shell command, prompt or command result. Native actions still need to obey assignment/project policy: GitHub write permissions themselves do not prevent merging.

Existing SSH Git deploy keys remain separate. For a new checkout that should use App credentials for Git, have the worker run `gh auth setup-git` and use the repository's HTTPS remote after verifying its identity. The delivery service does not rewrite repository remotes or personal Git configuration.

While the runtime is connected and healthy, AgentControl checks renewal every 30 seconds and replaces tokens within ten minutes of expiry. Failed issuance or delivery is marked Blocked and retried after two minutes. Configuration and delivery serialize, and state persists across web restarts. A previously issued credential can remain valid until its expiry even if renewal fails or a grant changes; it is not immediately revoked by replacement.

**Disable renewal** prevents further issuance/delivery by this integration. It retains the current credential and its displayed expiry. For immediate revocation, suspend or uninstall the App in GitHub. Disconnecting a runtime also suspends renewal, but does not revoke an already issued token. Keep this distinction when retiring containers.

## Validation and remaining work

Automated tests exercise JWT signatures/clock bounds, explicit scope, account mismatch, excess/missing access, expiry, error redaction, encrypted storage, authenticated/CSRF-protected settings, revision conflicts, disablement, and SSH delivery/rotation without replacing a personal login. These use simulated GitHub responses and disposable SSH fixtures. They do not prove live GitHub permissions.

With `HVO_GITHUB_CLI_TESTS=1`, the installed `gh` must also read an isolated fake credential produced by the actual writer. This caught a migration failure when the initial file omitted the App bot identity: CLI migration attempted a user lookup that installation credentials cannot satisfy. The writer includes both current and legacy token locations and the bot identity. CI runs this check; GitHub CLI must be installed for it.

The first live issue → PR → independent review → correction exercise awaits App installation. Durable publication intents and reconciliation of uncertain writes remain work in #44/#8; simply delivering a token does not make arbitrary `gh` writes exactly-once. Credential scope is currently per runtime, not per task/session. Automatic Dev Container provisioning remains tracked separately in #43.

References: [GitHub installation token API](https://docs.github.com/en/rest/apps/apps#create-an-installation-access-token-for-an-app), [installation authentication](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-as-a-github-app-installation), [GitHub CLI authentication](https://cli.github.com/manual/gh_auth_login).
