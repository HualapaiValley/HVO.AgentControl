# Dev Container and Docker contexts

The repository's original Dev Container setup uses Docker outside the container. Its recorded development host is `hvo-dev-03.hvo.lan`; the forwarded Docker socket and `default` context target that host. Test runtimes run as sibling containers. This recorded hostname is not a runtime registration or authorization to infer SSH credentials.

After changing Dev Container configuration, use **Dev Containers: Rebuild Container** in VS Code. Verify:

```bash
docker context ls
docker --context default info
docker compose version
```

Additional Docker systems can use named contexts, substituting an explicitly configured SSH user/host whose account can access that daemon:

```bash
docker context create other-system --docker "host=ssh://USER@HOST"
docker --context other-system info
docker context use other-system
docker context use default
```

Leave `DOCKER_HOST` and `DOCKER_CONTEXT` unset for ordinary context switching. Context definitions under `~/.docker` in the dev container must be recreated or separately preserved after rebuilding it. Bind mount source paths resolve on the selected Docker host; named volumes avoid accidental assumptions about shared workspace paths. AgentControl's own SSH runtime connections and mounted secrets are separate from Docker contexts.

See the [Dev Container Docker feature](https://github.com/devcontainers/features/tree/main/src/docker-outside-of-docker) and [Docker context documentation](https://docs.docker.com/engine/manage-resources/contexts/).
