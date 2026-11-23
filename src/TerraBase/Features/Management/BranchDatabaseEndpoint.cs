using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Features.Backup;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Contracts.Continuity;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class BranchDatabaseEndpoint(BranchFlow flow) : Endpoint<DatabaseInput, BranchDatabaseResponse>
{
    public override void Configure()
    {
        Post(ManagementRoute.Database + "/branches");
        Name("branchDatabase");
        Summary("Branches the database into a new database");
        Description("{\"name\", \"ttlSeconds\"?, \"target\"?}: without a target the branch copies the current state (201); target.bookmark branches at a causal "
            + "position (201); target.pointInTime asks the continuity service for a branch from the archive (202 with the restore operation).");
        Tags(ManagementRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<BranchDatabaseResponse> HandleAsync(DatabaseInput request, CancellationToken cancellationToken) =>
        await flow.BranchAsync(HttpContext.Caller, request, cancellationToken);
}

internal sealed class BranchFlow(DatabaseAccess access, ManagementService management, TerraBaseOptions options, TimeProvider time)
{
    public async ValueTask<BranchDatabaseResponse> BranchAsync(Caller caller, DatabaseInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await access.ManagedAsync(caller, request.Project, request.Database, Scopes.DatabasesWrite, cancellationToken) switch
        {
            ResolvedDatabase resolved when request.ExpectedId is { } expected && !string.Equals(expected, resolved.Database.Id.ToString(), StringComparison.Ordinal) =>
                new PreconditionFailed("the database's id is not the expected one", "database_id_mismatch"),
            ResolvedDatabase resolved => Target(request.Target) switch
            {
                NoTarget => await LocalAsync(resolved, request, CausalBookmark.Empty, cancellationToken),
                CausalBookmark bookmark => await LocalAsync(resolved, request, bookmark, cancellationToken),
                DateTimeOffset at => await ArchiveAsync(resolved, request, new RestoreTarget(at), cancellationToken),
                Validation invalid => invalid,
            },
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }

    private static TargetChoice Target(JsonElement? target)
    {
        if (target is not { ValueKind: JsonValueKind.Object } element)
        {
            return new NoTarget();
        }

        DateTimeOffset? pointInTime = element.TryGetProperty("pointInTime", out var at) && at.ValueKind == JsonValueKind.String && at.TryGetDateTimeOffset(out var parsed)
            ? parsed
            : null;
        var bookmark = element.TryGetProperty("bookmark", out var mark) && mark.ValueKind == JsonValueKind.String ? mark.GetString() : null;
        return RestoreTargets.Parse(pointInTime, bookmark);
    }

    private async ValueTask<BranchDatabaseResponse> LocalAsync(ResolvedDatabase source, DatabaseInput request, CausalBookmark at, CancellationToken cancellationToken) =>
        await management.BranchAsync(source.Project, source.Database, request.Name, request.TtlSeconds, at, cancellationToken) switch
        {
            DatabaseRecord created => new Created<DatabaseBody>(string.Concat("/v1/projects/", created.ProjectId.ToString(), "/databases/", created.Name),
                DatabaseBody.From(created, options.Region, time.GetUtcNow())),
            Validation invalid => invalid,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Conflict conflict => conflict,
            QuotaExceeded quota => quota,
            Unavailable unavailable => unavailable,
        };

    private async ValueTask<BranchDatabaseResponse> ArchiveAsync(ResolvedDatabase source, DatabaseInput request, RestoreTarget target, CancellationToken cancellationToken) =>
        await management.CopyAsync(source.Project, source.Database, request.Name, request.TtlSeconds, target, branch: true, cancellationToken) switch
        {
            RestoreOperation operation => new Accepted<RestoreOperationBody>(
                string.Concat("/v1/projects/", source.Project.Id.ToString(), "/databases/", source.Database.Id.ToString(), "/restores/", operation.OperationId.ToString()),
                RestoreOperationBody.From(operation)),
            NotFound missing => missing,
            Validation invalid => invalid,
            Conflict conflict => conflict,
            Unavailable unavailable => unavailable,
        };
}
