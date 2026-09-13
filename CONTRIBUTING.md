# Contributing

V2 development uses feature branches and pull requests targeting `main`.
Keep the V1 archive unchanged. Describe the change, validation and known limits
using the PR template; attach redacted screenshots for visible UI changes.

Run the commands in [AGENTS.md](AGENTS.md). CI checks build/tests/format,
container packaging, and the browser portal with the agent runtime disabled.
Live provider tests are separate, explicitly authorized checks.

Address review findings and resolve conversations before merging. Squash merge
is a reasonable default; merged branches are automatically deleted. Releases
are separate manual actions, never a side effect of merging a PR.

Track work with the bug/feature issue templates. Record relevant upstream
dependencies in [docs/EXTERNAL-ISSUES.md](docs/EXTERNAL-ISSUES.md).

Do not put secrets or sensitive transcripts in issues, PRs or screenshots.
Report sensitive security concerns privately to the repository owner.
