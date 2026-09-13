# Contributing

## Development setup

Use the included Dev Container, or install the SDK version specified in
`global.json`. Follow the complete [local setup](README.md#local-setup) before
starting the application. It initializes the required local owner secret and
sets the data, secrets, and loopback HTTP configuration without printing the
secret value.

## Validation

```bash
dotnet restore HVO.AgentControl.slnx
dotnet build HVO.AgentControl.slnx --no-restore --configuration Release --warnaserror
dotnet test HVO.AgentControl.slnx --no-build --configuration Release
dotnet format HVO.AgentControl.slnx --no-restore --verify-no-changes
```

Record the tested commit, commands, results and environment-gated skips in the PR. Code changes need local tests and independent review. Documentation/workflow-only changes need validation appropriate to those files; they do not require another full run of unchanged application tests.

Prerelease PR automation runs only the short `build` check once the PR is ready for review. Expensive integration checks are manually selectable and do not run again automatically after a merge. See [validation policy](docs/VALIDATION_POLICY.md) for commands, review requirements and deployment evidence.
