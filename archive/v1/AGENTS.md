# HVO.AgentControl Agent Guide

## Toolchain and validation

- Use the .NET SDK pinned in `global.json` and the `HVO.AgentControl.slnx` solution.
- Keep NuGet package versions centralized in `Directory.Packages.props`.
- Before handing off a change, run:

  ```bash
  dotnet restore HVO.AgentControl.slnx
  dotnet build HVO.AgentControl.slnx --no-restore --configuration Release --warnaserror
  dotnet test HVO.AgentControl.slnx --no-build --configuration Release
  dotnet format HVO.AgentControl.slnx --no-restore --verify-no-changes
  ```

## Repository structure

- Application projects belong in `src/`.
- Automated test projects belong in `tests/`.
- Architecture notes, decisions, and operational documentation belong in `docs/`.
- Keep Blazor component markup, code-behind, scoped CSS, and scoped JavaScript in sibling files.

## Regression and UI validation

- Add meaningful regression coverage for behavior changes and bug fixes; test outcomes and failure/recovery boundaries, not just implementation details.
- For Blazor/UI changes, update Playwright checks in `tests/Browser/` and exercise desktop/mobile behavior against the published application. `package-smoke.cjs` supports an empty isolated database; `ui-recovery.cjs` requires seeded worker data.
- During prerelease development, automatic CI is the short `build` check on a ready-for-review PR. Draft PRs skip it; marking a draft ready runs it. There is no duplicate push-to-main run. See [validation policy](docs/VALIDATION_POLICY.md).
- Workers still run the validation above before handing off code changes. Record the exact commit, commands, pass/fail/skip counts, and timeouts in the PR; a skipped fixture or timed-out suite is incomplete validation. For documentation/workflow-only changes, validate the changed files and workflow commands instead of repeating the unchanged application's full test suite.
- Obtain independent review of the proposed revision. Run the relevant manual integration suite when the change needs coverage unavailable on the worker, when the reviewer requests it, or before a deployment that depends on it. A successful short `build` check does not establish full integration coverage.
- Use disposable data and credentials for browser tests; do not submit mutations to an active owner deployment unless the task explicitly calls for live validation.
