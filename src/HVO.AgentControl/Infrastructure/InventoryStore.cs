using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HVO.AgentControl.Core;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed partial class ControlStore
{
    public Task<InventoryPage<HostRecord>> Hosts(long after = 0, int take = 50, bool includeArchived = false) => Read(async db =>
    {
        ValidateInventoryPage(after, take);
        var rows = await db.Hosts.AsNoTracking().Where(x => x.Sequence > after && (includeArchived || !x.Archived))
            .OrderBy(x => x.Sequence).Take(take + 1).ToListAsync();
        return new InventoryPage<HostRecord>(rows.Take(take).ToList(), rows.Count > take ? rows[take - 1].Sequence : null);
    });

    public Task<InventoryPage<ProjectRecord>> Projects(long after = 0, int take = 50, bool includeArchived = false) => Read(async db =>
    {
        ValidateInventoryPage(after, take);
        var rows = await db.Projects.AsNoTracking().Where(x => x.Sequence > after && (includeArchived || !x.Archived))
            .OrderBy(x => x.Sequence).Take(take + 1).ToListAsync();
        return new InventoryPage<ProjectRecord>(rows.Take(take).ToList(), rows.Count > take ? rows[take - 1].Sequence : null);
    });

    public Task<HostRecord> Host(string id) => Read(db => RequireHost(db, InventoryId(id)));
    public Task<ProjectRecord> Project(string id) => Read(db => RequireProject(db, InventoryId(id)));

    public Task<ProjectRecord> ProjectByRepository(string repositoryUrl) => Read(async db =>
    {
        var repository = CanonicalProjectRepository(repositoryUrl);
        return await db.Projects.AsNoTracking().SingleOrDefaultAsync(x => x.RepositoryUrl == repository)
            ?? throw new InventoryException("not_found", "Repository has no registered project.", 404);
    });

    public Task<InventoryMutationStatus> InventoryMutation(string requestId) => Read(async db =>
    {
        var id = InventoryId(requestId);
        var receipt = await db.InventoryMutations.FindAsync(id) ?? throw new InventoryException("not_found", "Inventory request not found.", 404);
        return new InventoryMutationStatus(receipt.RequestId, receipt.ResourceKind, receipt.ResourceId, receipt.Action, receipt.CreatedAt);
    });

    public Task<HostRecord> CreateHost(CreateHostInput input) => Write(async db =>
    {
        var id = InventoryId(input.Id);
        var name = InventoryText(input.Name, "name", 120, required: true);
        var description = InventoryText(input.Description, "description", 2000);
        if (input.Kind is not ("Unclassified" or "PhysicalMachine" or "VirtualMachine"))
            throw new InventoryException("validation", "Host kind must be Unclassified, PhysicalMachine or VirtualMachine.");
        return await MutateInventory(db, input.RequestId, "Host", id, "Create", new { name, input.Kind, description }, async () =>
        {
            if (await db.Hosts.AnyAsync(x => x.Id == id)) throw InventoryConflict("identity_exists", "Host identity already exists.");
            var host = new HostRecord { Id = id, Name = name, Kind = input.Kind, Description = description, CreatedAt = Now, UpdatedAt = Now };
            db.Hosts.Add(host);
            return host;
        });
    });

    public Task<HostRecord> UpdateHost(string hostId, UpdateHostInput input) => Write(async db =>
    {
        var id = InventoryId(hostId);
        var name = InventoryText(input.Name, "name", 120, required: true);
        var description = InventoryText(input.Description, "description", 2000);
        return await MutateInventory(db, input.RequestId, "Host", id, "Update", new { input.ExpectedRevision, name, description }, async () =>
        {
            var host = await RequireHost(db, id);
            RequireInventoryRevision(host.Revision, input.ExpectedRevision);
            host.Name = name; host.Description = description; host.Revision++; host.UpdatedAt = Now;
            return host;
        });
    });

    public Task<HostRecord> ArchiveHost(string hostId, ArchiveInventoryInput input) => Write(async db =>
    {
        var id = InventoryId(hostId);
        return await MutateInventory(db, input.RequestId, "Host", id, "Archive", new { input.ExpectedRevision, input.Archived }, async () =>
        {
            var host = await RequireHost(db, id);
            RequireInventoryRevision(host.Revision, input.ExpectedRevision);
            if (input.Archived && await db.RuntimeEnvironments.AnyAsync(x => x.HostId == id))
                throw InventoryConflict("resource_in_use", "Reset or reassign this host's runtime environment associations before archiving it.");
            host.Archived = input.Archived; host.Revision++; host.UpdatedAt = Now;
            return host;
        });
    });

    public Task<ProjectRecord> CreateProject(CreateProjectInput input) => Write(async db =>
    {
        var id = InventoryId(input.Id);
        var name = InventoryText(input.Name, "name", 120, required: true);
        var repository = CanonicalProjectRepository(input.RepositoryUrl);
        var branch = InventoryBranch(input.BaseBranch);
        var description = InventoryText(input.Description, "description", 2000);
        return await MutateInventory(db, input.RequestId, "Project", id, "Create", new { name, repository, branch, description }, async () =>
        {
            if (await db.Projects.AnyAsync(x => x.Id == id)) throw InventoryConflict("identity_exists", "Project identity already exists.");
            if (await db.Projects.AnyAsync(x => x.RepositoryUrl == repository))
                throw InventoryConflict("repository_exists", "This repository already has a project, possibly archived. Reuse that project identity.");
            var project = new ProjectRecord
            {
                Id = id,
                Name = name,
                RepositoryUrl = repository,
                BaseBranch = branch,
                Description = description,
                CreatedAt = Now,
                UpdatedAt = Now
            };
            db.Projects.Add(project);
            return project;
        });
    });

    public Task<ProjectRecord> UpdateProject(string projectId, UpdateProjectInput input) => Write(async db =>
    {
        var id = InventoryId(projectId);
        var name = InventoryText(input.Name, "name", 120, required: true);
        var branch = InventoryBranch(input.BaseBranch);
        var description = InventoryText(input.Description, "description", 2000);
        return await MutateInventory(db, input.RequestId, "Project", id, "Update", new { input.ExpectedRevision, name, branch, description }, async () =>
        {
            var project = await RequireProject(db, id);
            RequireInventoryRevision(project.Revision, input.ExpectedRevision);
            project.Name = name; project.BaseBranch = branch; project.Description = description; project.Revision++; project.UpdatedAt = Now;
            return project;
        });
    });

    public Task<ProjectRecord> ArchiveProject(string projectId, ArchiveInventoryInput input) => Write(async db =>
    {
        var id = InventoryId(projectId);
        return await MutateInventory(db, input.RequestId, "Project", id, "Archive", new { input.ExpectedRevision, input.Archived }, async () =>
        {
            var project = await RequireProject(db, id);
            RequireInventoryRevision(project.Revision, input.ExpectedRevision);
            if (input.Archived && await db.RuntimeEnvironments.AnyAsync(x => x.ConfigurationProjectId == id))
                throw InventoryConflict("resource_in_use", "Reset or change runtime configuration sources referencing this project before archiving it.");
            if (input.Archived && await db.TaskBindings.AnyAsync(x => x.ProjectId == id && x.State == TaskBindingState.Active))
                throw InventoryConflict("resource_in_use", "Release active task bindings referencing this project before archiving it.");
            project.Archived = input.Archived; project.Revision++; project.UpdatedAt = Now;
            return project;
        });
    });

    private static async Task<T> MutateInventory<T>(ControlDb db, string requestId, string kind, string id, string action, object input, Func<Task<T>> mutate)
    {
        var request = InventoryId(requestId);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Write(input))));
        if (await db.InventoryMutations.FindAsync(request) is { } prior)
        {
            if (prior.ResourceKind != kind || prior.ResourceId != id || prior.Action != action || prior.RequestHash != hash)
                throw InventoryConflict("idempotency_conflict", "This request ID belongs to a different inventory mutation.");
            return Json.Read<T>(prior.ResultJson);
        }

        var result = await mutate();
        // Obtain the generated sequence before recording the immutable replay result.
        // Both saves and the event commit in ControlStore.Write's single transaction.
        await db.SaveChangesAsync();
        db.InventoryMutations.Add(new InventoryMutationReceipt
        {
            RequestId = request,
            ResourceKind = kind,
            ResourceId = id,
            Action = action,
            RequestHash = hash,
            ResultJson = Json.Write(result),
            CreatedAt = Now
        });
        Event(db, "Inventory" + action, payload: new { requestId = request, resourceKind = kind, resourceId = id }, provenance: "user");
        return result;
    }

    private static async Task<HostRecord> RequireHost(ControlDb db, string id) =>
        await db.Hosts.SingleOrDefaultAsync(x => x.Id == id) ?? throw new InventoryException("not_found", "Host not found.", 404);

    private static async Task<ProjectRecord> RequireProject(ControlDb db, string id) =>
        await db.Projects.SingleOrDefaultAsync(x => x.Id == id) ?? throw new InventoryException("not_found", "Project not found.", 404);

    private static void RequireInventoryRevision(long actual, long expected)
    {
        if (expected < 1) throw new InventoryException("validation", "Expected revision must be positive.");
        if (actual != expected) throw InventoryConflict("revision_conflict", "Inventory changed; refresh before editing.");
    }

    private static InventoryException InventoryConflict(string code, string message) => new(code, message, 409);

    private static string InventoryId(string? value)
    {
        if (value is null || value.Length > 36 || !Guid.TryParse(value, out var id) || id == Guid.Empty)
            throw new InventoryException("validation", "A non-empty UUID resource/request identity is required.");
        return id.ToString("N");
    }

    private static void ValidateInventoryPage(long after, int take)
    {
        if (after < 0 || take is < 1 or > 100) throw new InventoryException("validation", "Use a non-negative after cursor and take between 1 and 100.");
    }

    private static string InventoryText(string? text, string field, int limit, bool required = false)
    {
        if (text is null || text.Length > limit || text.Any(char.IsControl) || (required && string.IsNullOrWhiteSpace(text)))
            throw new InventoryException("validation", $"Invalid {field}; maximum {limit} characters and no control characters.");
        return text.Trim();
    }

    private static string InventoryBranch(string? value)
    {
        var branch = InventoryText(value, "base branch", 240, required: true);
        if (branch == "@" || branch.StartsWith('-') || branch.Contains("..", StringComparison.Ordinal) || branch.Contains("@{", StringComparison.Ordinal) ||
            branch.Any(c => char.IsWhiteSpace(c) || "~^:?*[\\".Contains(c)) ||
            branch.Split('/').Any(x => x.Length == 0 || x.StartsWith('.') || x.EndsWith('.') || x.EndsWith(".lock", StringComparison.Ordinal)))
            throw new InventoryException("validation", "Base branch must be a literal Git branch name.");
        return branch;
    }

    public static string CanonicalProjectRepository(string? value)
    {
        // Deliberately support github.com only until other providers have an explicit
        // identity/access contract. No URI cleanup that could hide credentials or traversal.
        var repository = InventoryText(value, "repository URL", 400, required: true);
        const string https = "https://github.com/", ssh = "git@github.com:";
        var path = repository.StartsWith(https, StringComparison.OrdinalIgnoreCase) ? repository[https.Length..] :
            repository.StartsWith(ssh, StringComparison.OrdinalIgnoreCase) ? repository[ssh.Length..] : "";
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        var parts = path.Split('/');
        if (parts.Length != 2 || !Regex.IsMatch(parts[0], "\\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?\\z") ||
            !Regex.IsMatch(parts[1], "\\A[A-Za-z0-9_.-]{1,100}\\z") || parts[1] is "." or "..")
            throw new InventoryException("validation", "Use an explicit github.com HTTPS or git@github.com:owner/repository clone URL without credentials, query or fragment.");
        return https + path.ToLowerInvariant();
    }
}
