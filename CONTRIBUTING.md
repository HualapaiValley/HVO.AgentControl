# Contributing

## Development setup

Use the included Dev Container, or install the SDK version specified in
`global.json`.

```bash
dotnet restore HVO.AgentControl.slnx
dotnet run --project src/HVO.AgentControl
```

## Validation

```bash
dotnet build HVO.AgentControl.slnx --configuration Release --warnaserror
dotnet format HVO.AgentControl.slnx --verify-no-changes
```
