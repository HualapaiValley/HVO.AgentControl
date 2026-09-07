# HVO.AgentControl Agent Guide

## Toolchain and validation

- Use the .NET SDK pinned in `global.json` and the `HVO.AgentControl.slnx` solution.
- Keep NuGet package versions centralized in `Directory.Packages.props`.
- Before handing off a change, run:

  ```bash
  dotnet restore HVO.AgentControl.slnx
  dotnet build HVO.AgentControl.slnx --no-restore --configuration Release --warnaserror
  dotnet format HVO.AgentControl.slnx --no-restore --verify-no-changes
  ```

## Repository structure

- Application projects belong in `src/`.
- Automated test projects belong in `tests/`.
- Architecture notes, decisions, and operational documentation belong in `docs/`.
- Keep Blazor component markup, code-behind, scoped CSS, and scoped JavaScript in sibling files.
