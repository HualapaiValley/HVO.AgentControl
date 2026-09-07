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
- CI must pass on the exact proposed revision, including unit/persistence tests, SSH/recovery fixtures, and the published UI smoke job. Report any environment-gated skips and remaining validation limitations in the PR.
- Use disposable data and credentials for browser tests; do not submit mutations to an active owner deployment unless the task explicitly calls for live validation.
