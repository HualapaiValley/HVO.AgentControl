# Contributing

## Development setup

Use the included Dev Container, or install the SDK version specified in
`global.json`. Follow the complete [local setup](README.md#local-setup) before
starting the application. It initializes the required local owner secret and
sets the data, secrets, and loopback HTTP configuration without printing the
secret value.

## Validation

```bash
dotnet build HVO.AgentControl.slnx --configuration Release --warnaserror
dotnet format HVO.AgentControl.slnx --verify-no-changes
```
