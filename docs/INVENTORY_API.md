# Host and project inventory API

This is the first implemented slice of #121 and [the execution environment plan](EXECUTION_ENVIRONMENT_PLAN.md). The inventory is additive: installing it creates three tables and does not infer or rewrite any existing runtime, worker, command, credential, directory, native session or transcript binding. Legacy `Worker.Project` display labels remain display labels. A missing project must be registered with an explicit repository URL; it is never guessed from that label or a working directory.

## Resource contract

`HostRecord` identifies an owner-registered machine/VM independently of an SSH endpoint or Docker container. It contains `id`, `sequence`, `name`, `kind`, `description`, `archived`, `revision`, `createdAt` and `updatedAt`. Kind is owner-declared `Unclassified` (default), `PhysicalMachine` or `VirtualMachine`; it is not verified platform, capacity or Docker authority. Registering a host does not connect to it, make it schedulable, grant credentials, or authorize remote effects. Runtime/host verification and bindings follow in the next slice.

`ProjectRecord` contains the same metadata except kind, plus immutable `repositoryUrl` and mutable `baseBranch`. This initial repository identity adapter supports github.com HTTPS and `git@github.com:owner/repository` clone URLs, with an optional `.git` suffix. Stored identity is `https://github.com/owner/repository`, lowercase, without `.git`. Owner/repository shorthand, other providers/enterprise hosts, credentials in URLs, queries, fragments, traversal and branch/issue URLs are rejected. Repository access, actual existence and branch existence have not been verified by registration. Repository transfers/renames need a future verified identity-migration contract; updates cannot redirect an existing project to another repository.

One canonical repository has one project identity, including archived projects. Use workgroups/policies to organize that project later. Host names and project names are display values and need not be unique. Host kind cannot be changed through the metadata edit endpoint; verified classification is follow-up work. Names are required and limited to 120 characters, descriptions to 2,000, repository input to 400 and base branch to 240. Text cannot contain control characters; base branch must be a literal Git branch name, not a revision expression.

IDs and request IDs are non-empty UUIDs, normalized to 32 lowercase hexadecimal characters. Revisions begin at 1. The database-generated sequence is a stable pagination cursor, not a public identity or an update revision. Timestamps are UTC Unix milliseconds. Clients cannot supply timestamps, sequence, revision or verification evidence through creation DTOs. Unknown JSON fields are rejected, including attempts to update immutable fields.

## Implemented routes

All routes have prefix `/api/v1`. They use the same `ControlStore` service boundary available to future UI/coordinator/MCP consumers. This table and the DTOs in `Core/InventoryModels.cs` are the explicit contract; generated OpenAPI coverage is not yet implemented.

| Method and route | Input / response |
| --- | --- |
| `GET /hosts` | `after=0`, `take=50` (1–100), `includeArchived=false`; `InventoryPage<HostRecord>` |
| `GET /hosts/{id}` | Host detail, including an archived host, or 404 |
| `POST /hosts` | `CreateHostInput`; 201 and `Location`, with committed host detail |
| `PUT /hosts/{id}` | `UpdateHostInput`; 200 with committed host detail |
| `POST /hosts/{id}/archive` | `ArchiveInventoryInput`; 200 with committed host detail |
| `GET /projects` | Same bounded query parameters; `InventoryPage<ProjectRecord>` |
| `GET /projects/{id}` | Project detail, including an archived project, or 404 |
| `GET /projects/by-repository?repositoryUrl=...` | URL-encoded clone URL; canonical identity lookup, including archived project, or 404 |
| `POST /projects` | `CreateProjectInput`; 201 and `Location`, with committed project detail |
| `PUT /projects/{id}` | `UpdateProjectInput`; 200 with committed project detail |
| `POST /projects/{id}/archive` | `ArchiveInventoryInput`; 200 with committed project detail |
| `GET /inventory/requests/{requestId}` | Committed request identity, resource kind/ID, action and timestamp, or 404 |

List responses contain `items` and nullable `nextAfter`. Continue using `nextAfter` with the same filters; null means no more matching rows at that read. Pages are bounded live reads, not a snapshot of an unchanging inventory; restart enumeration to see records unarchived behind the cursor. Use detail reads and existing durable `/events` to follow edits. Inventory events contain request/resource identity and action, without full descriptions or request bodies.

The current owner cookie authentication and antiforgery protection apply to every route: reads require authentication; mutations additionally require `X-CSRF-TOKEN` obtained from the existing `/api/v1/csrf` exchange. They do not provide new headless tokens or per-project access grants. Scoped automation authentication, rotation/revocation and project/host authorization remain explicit follow-up requirements.

## Mutation examples and retries

Create a project using an authenticated request with the antiforgery header:

```http
POST /api/v1/projects
Content-Type: application/json
X-CSRF-TOKEN: <owner antiforgery token>

{
  "requestId": "d2d31374-8cbd-4a59-8858-24eb0794b818",
  "id": "f2b33f8a-d519-4938-a5f0-e9bedc702cc4",
  "name": "AgentControl",
  "repositoryUrl": "https://github.com/RoySalisbury/HVO.AgentControl.git",
  "baseBranch": "main",
  "description": "Agent coordination development"
}
```

Edit using a new request identity and the latest revision:

```json
{
  "requestId": "24e9451e-97fa-4e2c-ad65-87a0b7d25f3a",
  "expectedRevision": 1,
  "name": "AgentControl beta",
  "baseBranch": "main",
  "description": "Agent coordination development"
}
```

Host creation uses `requestId`, `id`, `name`, optional `kind` and optional `description`. Host editing uses `requestId`, `expectedRevision`, `name` and optional `description`. Archive/unarchive uses `requestId`, `expectedRevision` and `archived` (default true).

Every successful mutation stores its original result and a hash of normalized intent in `InventoryMutationReceipt`, in the same transaction as the resource change and journal event. Retrying the same request returns that original result and original success status, even after controller restart or later edits. It does not reapply the edit, increment revision or emit another event. A replay result may have an older revision than current detail; fetch detail before starting another edit. A request ID reused for a different resource, action or normalized payload conflicts, including changed expected revision. Use one UUID per intended mutation across both resource types.

After a lost response, query `/inventory/requests/{requestId}` and/or resubmit the identical request to retrieve the original result. A 404 means no receipt was committed at that read; it does not prove another concurrent request will not commit. Resubmission of the same ID is safe in either case. Failed validation/conflicts roll back and do not reserve request IDs. Receipts and identities are retained without automatic pruning in this slice; no public purge endpoint exists.

Store-level errors use `{ "error": "actionable message", "code": "stable_code" }`: `validation` (400), `not_found` (404), and `identity_exists`, `repository_exists`, `revision_conflict`, `idempotency_conflict` (409). Authentication/CSRF, malformed JSON and parameter-binding errors retain the existing middleware/framework responses; clients must always inspect HTTP status. No remote operation is started here, so successful inventory writes are synchronous 200/201. Asynchronous provisioning will use separate durable operation IDs and 202 responses.

## Archive and remaining lifecycle work

Archive only changes inventory visibility while preserving identity and retry/history evidence. Unarchive uses the same endpoint with `archived=false`. There is no DELETE, remote stop, container removal, retirement or purge in this slice. Existing workers/runtimes are unbound and continue operating exactly as before. Before consumers can schedule from these records, they must enforce archive eligibility and the binding/ownership rules below; hiding a host is not draining its workers.

| Capability | Implementation status |
| --- | --- |
| Persistent host/project inventory, canonical repository lookup, bounded REST reads, metadata edits/archive, revisions and retry receipts | Implemented here |
| Runtime kind and verified host/container binding; logical worker slot; legacy conversation binding; project-qualified task/workspace/session identities | Next #121 slice; no backfill or guessed hosting in this migration |
| Capabilities/provisioner credentials, requested/resolved/observed settings and physical resource reservations | Verification and #6; registration is not evidence |
| Repository access, configuration, isolated task preparation and fresh native sessions | #42; existing WorkItem/phase ownership remains authoritative |
| UI creation wizard, provision/drain/restart/retire operations and disposable devcontainer lifecycle tests | #43; no new UI flow or Docker adapter here |
| Scoped automation authentication and generated OpenAPI | Outstanding public API requirements |
| Canary and progressive live-fleet transition | Only after verified provisioning/recovery acceptance |

Tests exercise owner authentication/CSRF, structured conflicts, canonical duplicate detection, concurrent edits/retries, archive/pagination, immutable-field rejection, restart replay and an upgrade from the preceding schema with retained legacy native-session/command/transcript evidence. Standard exact-head build, persistence, SSH/recovery and published UI CI remain required.
