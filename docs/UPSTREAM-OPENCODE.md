# OpenCode Upstream Reporting

Repository: https://github.com/anomalyco/opencode

Live status for every related or submitted issue lives in the canonical
[external issue tracker](EXTERNAL-ISSUES.md). Do not duplicate status, issue
bodies or draft narratives here; the tracker is the single source. **All
currently known reports have already been submitted** — search before filing,
and do not re-file an existing item.

## Filing

- Issue chooser: https://github.com/anomalyco/opencode/issues/new/choose
- Bug template: https://github.com/anomalyco/opencode/issues/new?template=bug-report.yml
- Feature template: https://github.com/anomalyco/opencode/issues/new?template=feature-request.yml
- Contribution rules: https://github.com/anomalyco/opencode/blob/dev/CONTRIBUTING.md

Guidance: search for duplicates, use a template, keep reports short. Bug reports
request description, version, plugins, reproduction steps, OS, terminal and an
optional screenshot/share link. Features require a duplicate-search confirmation
and description. UI/core features need maintainer design approval before
implementation; PRs must reference an issue.

## Safety

- Do not publish full conversations or raw authenticated logs.
- Screenshot capture works locally; redact credentials, private repository
  content and personal information first.
- State the tested version explicitly. Evidence for OpenCode 1.18.30 is not a
  claim about every release or current `dev`.
- Never create or edit upstream issues, comments, labels or PRs from automated
  documentation or agent flows without explicit owner direction.

## Local evidence

- `artifacts/model-sync-live/model-sync-live-results.json`
- `artifacts/model-sync-live/portal-after-direction1.png`
- `artifacts/model-sync-live/portal-after-direction2.png`
- `artifacts/model-sync-live/tui-picker-direction2.txt`

These artifacts are local and git-ignored. Review and redact before attaching
anything through GitHub's screenshot field; local filesystem paths are not
accessible to upstream maintainers. No upstream PR has been created.
