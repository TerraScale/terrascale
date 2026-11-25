using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
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
        var outcome = await access.ManagedAsync(caller, request.Project, request.Database, Scopes.DatabasesWrite, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase resolved when request.ExpectedId is { } expected && !string.Equals(expected, resolved.Database.Id.ToString(), StringComparison.Ordinal) =>
                new DatabaseIdMismatch(),
            ResolvedDatabase resolved when DatabaseAccess.IsNarrowed(resolved.Caller) => DatabaseAccess.NarrowedCreate,
            ResolvedDatabase resolved => Target(request.Target) switch
            {
                NoTarget => await LocalAsync(resolved, request, CausalBookmark.Empty, cancellationToken),
                CausalBookmark bookmark => await LocalAsync(resolved, request, bookmark, cancellationToken),
                DateTimeOffset at => await ArchiveAsync(resolved, request, new RestoreTarget(at), cancellationToken),
                InvalidInput invalid => invalid,
            },
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
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

    private async ValueTask<BranchDatabaseResponse> LocalAsync(ResolvedDatabase source, DatabaseInput request, CausalBookmark at, CancellationToken cancellationToken)
    {
        var ready = await access.CaughtUpAsync(source.Database, cancellationToken);
        return ready switch
        {
            ISqlDatabaseSession session => await management.BranchAsync(source.Project, source.Database, session, request.Name, request.TtlSeconds, at, cancellationToken) switch
            {
                DatabaseRecord created => new Created<DatabaseBody>(string.Concat("/v1/projects/", created.ProjectId.ToString(), "/databases/", created.Name),
                    DatabaseBody.From(created, options.Region, time.GetUtcNow())),
                InvalidInput invalid => invalid,
                CallerForbidden forbidden => forbidden,
                ResourceNotFound missing => missing,
                StateConflict conflict => conflict,
                BackendUnavailable unavailable => unavailable,
            },
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<BranchDatabaseResponse> ArchiveAsync(ResolvedDatabase source, DatabaseInput request, RestoreTarget target, CancellationToken cancellationToken)
    {
        var outcome = await management.CopyAsync(source.Project, source.Database, request.Name, request.TtlSeconds, target, branch: true, cancellationToken);
        return outcome switch
        {
            RestoreOperation operation => new Accepted<RestoreOperationBody>(
                string.Concat("/v1/projects/", source.Project.Id.ToString(), "/databases/", source.Database.Id.ToString(), "/restores/", operation.OperationId.ToString()),
                RestoreOperationBody.From(operation)),
            ResourceNotFound missing => missing,
            InvalidInput invalid => invalid,
            StateConflict conflict => conflict,
            BackendUnavailable unavailable => unavailable,
        };
    }
}
