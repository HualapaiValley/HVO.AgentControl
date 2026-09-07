# macOS command PATH — 2026-09-07

The owner's Home M4 runtime incorrectly reported missing `tmux`. A read-only SSH exec probe using the saved credential and pinned host key established:

- Platform: `Darwin arm64`.
- SSH command PATH: `/usr/bin:/bin:/usr/sbin:/sbin`.
- Installed binary: `/opt/homebrew/bin/tmux`, version `3.6a`.

The interactive login's environment was not available to SSH exec. AgentControl now runs its SSH command bodies explicitly through a POSIX shell with a shared environment prelude. On macOS it appends existing standard Homebrew bin/sbin directories, retaining any explicitly configured PATH precedence. Bootstrap and the OpenCode launcher also include the prelude, so a tmux server's older environment cannot cause the launcher to lose tool discovery. Verification displays each prerequisite's detected path. No shell dotfiles were sourced/changed and no Mac packages were installed.

Validation:

- Solution restore, Release build with warnings as errors, and format verification passed.
- Full regression with SSH fixtures: **28 passed, 0 failed, 2 optional native-provider tests skipped**, `macos-command-path.trx` (1 minute 3 seconds).
- A new behavioral test executes the POSIX wrapper and confirms preservation of an explicit PATH prefix and literal arguments containing quotes, newlines and command-substitution syntax. Existing SSH bootstrap/workspace tests also exercise the shared wrapper.
- Read-only probes on the actual Mac confirmed discovery in the normalized SSH command environment and in an independent child shell starting from the minimal system PATH, using the same prelude as the launcher.
- After Docker deployment, the application's authenticated verification API returned **Verified** for Home M4. All checks passed: SSH, SFTP, platform, tmux, lsof, curl, git, workspace, bootstrap directory, API port, and installer prerequisites. OpenCode was absent and the profile's existing install-if-missing option permits pinned installation when the owner connects.
- The new readiness result was saved through the setup-only path, retaining the saved credential and disconnected runtime. No OpenCode process was started on the Mac.
- Published application readiness, authentication, interactive navigation/reloads, form interaction and browser assets were checked after deployment. SSH fixtures were stopped after regression testing.

Compact live check results are retained locally in `.fixture/home-m4-path-verification.json`; credentials are not included. Already-running remote processes retain their own environment until deliberately restarted. Nonstandard tool installations still require an appropriate SSH PATH or configured executable location.
