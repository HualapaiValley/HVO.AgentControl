# AgentControl V2 Development

The repository root is V2. `archive/v1/` is historical reference only; its
instructions do not govern V2. Do not modify the archive, reactivate V1
workflows, or run old deployment scripts without explicit owner direction.

## Scope

Build a Docker-native controller for self-contained OpenCode workers using ACP.
No Fleet dependency, Claude-specific adapter, shared worker checkout, or
implicit reuse of existing infrastructure. The baseline does not implement
the planned worker lifecycle yet; distinguish planned from tested behavior.

## Validation

Use the SDK pinned in `global.json`; package versions belong in
`Directory.Packages.props`. Active code belongs in `src/`, tests in `tests/`,
and design notes in `docs/`.

```bash
dotnet restore HVO.AgentControl.slnx
dotnet build HVO.AgentControl.slnx --no-restore -c Release --warnaserror
dotnet test HVO.AgentControl.slnx --no-build -c Release
dotnet format HVO.AgentControl.slnx --no-restore --verify-no-changes
```

Test outcomes and failure/recovery boundaries. Receipt is not execution;
turn completion is not verified task success; cancellation is not rollback.
Do not retry uncertain writes without reconciling effects.

Never commit credentials, runtime databases, provider transcripts, or local
configuration. Use disposable resources for integration tests. Commits/pushes
and production operations require explicit owner authorization.
