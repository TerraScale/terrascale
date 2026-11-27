using TerraScale.Platform.Features.Functions;

namespace TerraScale.Platform.Features.FileStorage;

public sealed record ProjectClosed;

public sealed record ProjectMarkedClosed(IReadOnlyList<string> Regions);

public sealed record FileProjectOrganizationMismatch;

public sealed record ClosedFileRowsSwept;

public sealed record ClosedFileRowsIdle;

public readonly union MarkProjectClosedOutcome(ProjectMarkedClosed, FileProjectOrganizationMismatch, PlatformUnavailable);

public readonly union EraseProjectFilesOutcome(ProjectClosed, PlatformUnavailable);

public readonly union SweepClosedFileRowsOutcome(ClosedFileRowsSwept, ClosedFileRowsIdle, PlatformUnavailable);

public sealed class ProjectFileErasure(PlatformStore store, S3FileObjectStore objects, TimeProvider timeProvider)
{
    internal const string RegionsQuery = "SELECT region FROM platform_file_regions ORDER BY region";

    private const int RowBatch = 256;
    private const int CloseFunctionBatch = 250;
    private const int ByteBudget = 2 * 1024 * 1024;
    private const int CloseRounds = 16;
    private const int ConflictAttempts = 8;
    private const string SerializationConflict = "serialization_conflict";
    private const string Closed = "EXISTS (SELECT 1 FROM platform_closed_file_projects c WHERE c.project_id = ? AND c.organization_id = ?)";
    private static readonly string CloseFunctions = $"WHERE f.project_id = ? AND f.organization_id = ? AND {Closed} AND {FunctionService.TrackedDatabase} ORDER BY f.rowid LIMIT {CloseFunctionBatch}";
    private static readonly string RecordCloseFunctionDatabases = "INSERT INTO platform_function_databases (database_id, project_id, organization_id) "
        + "SELECT r.database_id, r.project_id, r.organization_id FROM (SELECT DISTINCT f.database_id, f.project_id, f.organization_id FROM platform_functions f "
        + $"WHERE f.project_id = ? AND f.organization_id = ? AND {Closed} "
        + $"AND NOT {FunctionService.TrackedDatabase} ORDER BY f.database_id LIMIT {CloseFunctionBatch}) r WHERE true {FunctionService.AdoptDatabase}";
    private static readonly string FenceCloseFunctionDatabases = "UPDATE platform_function_databases SET delete_fence = ? WHERE database_id IN ("
        + $"SELECT f.database_id FROM platform_functions f {CloseFunctions})";
    private static readonly string DeleteCloseFunctions = "DELETE FROM platform_functions WHERE rowid IN ("
        + "SELECT row_id FROM (SELECT row_id, size, SUM(size) OVER (ORDER BY row_id ROWS UNBOUNDED PRECEDING) AS total FROM ("
        + $"SELECT f.rowid AS row_id, {FunctionService.FunctionBytes} AS size FROM platform_functions f "
        + $"{CloseFunctions})) WHERE total = size OR total <= {ByteBudget})";
    private const string FileBytes = "octet_length(project_id) + octet_length(bucket_id) + octet_length(id) + octet_length(organization_id) "
        + "+ octet_length(body) + octet_length(status) + 16";
    private const string BucketBytes = "octet_length(b.project_id) + octet_length(b.id) + octet_length(b.organization_id) + octet_length(b.name) "
        + "+ octet_length(b.region) + octet_length(b.rules) + coalesce(octet_length(b.delete_fence), 0)";
    private const string PendingProject = "SELECT c.project_id, c.organization_id FROM platform_closed_file_projects c "
        + "WHERE EXISTS (SELECT 1 FROM platform_files f WHERE f.project_id = c.project_id) "
        + "OR EXISTS (SELECT 1 FROM platform_buckets b WHERE b.project_id = c.project_id) LIMIT 1";

    private static readonly string[] EraseSteps =
    [
        "DELETE FROM platform_files WHERE project_id = ? AND (bucket_id, id) IN (SELECT bucket_id, id FROM (SELECT bucket_id, id, size, "
            + "SUM(size) OVER (ORDER BY bucket_id, id ROWS UNBOUNDED PRECEDING) AS total FROM (SELECT bucket_id, id, " + FileBytes + " AS size "
            + $"FROM platform_files WHERE project_id = ? AND {Closed} ORDER BY bucket_id, id LIMIT {RowBatch})) "
            + $"WHERE total = size OR total <= {ByteBudget})",
        "DELETE FROM platform_buckets WHERE project_id = ? AND id IN (SELECT id FROM (SELECT id, size, "
            + "SUM(size) OVER (ORDER BY id ROWS UNBOUNDED PRECEDING) AS total FROM (SELECT b.id, " + BucketBytes + " AS size "
            + $"FROM platform_buckets b WHERE b.project_id = ? AND {Closed} "
            + "AND NOT EXISTS (SELECT 1 FROM platform_files f WHERE f.project_id = b.project_id AND f.bucket_id = b.id) "
            + $"ORDER BY b.id LIMIT {RowBatch})) WHERE total = size OR total <= {ByteBudget})",
    ];

    public async ValueTask<MarkProjectClosedOutcome> MarkClosedAsync(Ulid project, Ulid organization, CancellationToken cancellationToken)
    {
        var projectId = SqlValues.Text(project.ToString());
        var organizationId = SqlValues.Text(organization.ToString());
        var closed = await CloseStrongAsync(projectId, organizationId, cancellationToken);
        return closed switch
        {
            SqlExecuteResponse { Results: var results } when results[1].Rows.Count == 0 => new FileProjectOrganizationMismatch(),
            SqlExecuteResponse { Results: var results } => new ProjectMarkedClosed(Regions(results[5])),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<PlatformStoreOutcome> CloseStrongAsync(
        JsonElement projectId,
        JsonElement organizationId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= ConflictAttempts; attempt++)
        {
            var now = timeProvider.GetUtcNow();
            var closedAt = SqlValues.Number(now.UnixMs());
            IReadOnlyList<SqlHttpStatement> statements =
            [
                SqlValues.Statement(
                    "INSERT OR IGNORE INTO platform_closed_file_projects (project_id, organization_id, closed_at) "
                    + "SELECT ?, ?, MAX(?, COALESCE((SELECT MAX(upload_expires_at) FROM platform_files WHERE project_id = ?), 0), "
                    + "COALESCE((SELECT MAX(purge_after) FROM platform_file_purges WHERE project_id = ?), 0)) "
                    + "WHERE NOT EXISTS (SELECT 1 FROM platform_buckets WHERE project_id = ? AND organization_id <> ?) "
                    + "AND NOT EXISTS (SELECT 1 FROM platform_bucket_regions WHERE project_id = ? AND organization_id <> ?) "
                    + "AND NOT EXISTS (SELECT 1 FROM platform_functions WHERE project_id = ? AND organization_id <> ?) "
                    + "AND NOT EXISTS (SELECT 1 FROM platform_function_databases WHERE project_id = ? AND organization_id <> ?)",
                    projectId, organizationId, closedAt, projectId, projectId,
                    projectId, organizationId, projectId, organizationId, projectId, organizationId, projectId, organizationId),
                SqlValues.Statement("SELECT 1 WHERE " + Closed, projectId, organizationId),
                SqlValues.Statement(RecordCloseFunctionDatabases, projectId, organizationId, projectId, organizationId),
                SqlValues.Statement(FenceCloseFunctionDatabases, FunctionService.Fence(now), projectId, organizationId, projectId, organizationId),
                SqlValues.Statement(DeleteCloseFunctions, projectId, organizationId, projectId, organizationId),
                SqlValues.Statement(RegionsQuery),
                FunctionService.FenceClosed(projectId, organizationId, now),
            ];
            var outcome = await store.ExecuteAsync(statements, SqlContext.Strong, cancellationToken);
            var conflicted = outcome switch
            {
                PlatformConflict { Code: SerializationConflict } => true,
                SqlExecuteResponse => false,
                PlatformConflict => false,
                PlatformUnavailable => false,
            };
            if (!conflicted)
            {
                return outcome;
            }
        }

        return new PlatformUnavailable("the project closure kept conflicting with concurrent writes; retry", "platform_state");
    }

    public async ValueTask<SweepClosedFileRowsOutcome> SweepClosedRowsAsync(CancellationToken cancellationToken)
    {
        var pending = await store.ReadAsync(SqlValues.Statement(PendingProject), cancellationToken);
        var target = pending switch
        {
            SqlExecuteResponse { Results: var results } => new SweepTarget(results[0].FirstRow(), null),
            PlatformConflict conflict => new SweepTarget(null, new PlatformUnavailable(conflict.Message, "platform_state")),
            PlatformUnavailable unavailable => new SweepTarget(null, unavailable),
        };
        if (target.Failure is { } failure)
        {
            return failure;
        }

        if (target.Row is not { } row)
        {
            return new ClosedFileRowsIdle();
        }

        var progress = await EraseRoundAsync(row.Text(0), row.Text(1), cancellationToken);
        if (progress.Failure is { } eraseFailure)
        {
            return eraseFailure;
        }

        return progress.More ? new ClosedFileRowsSwept() : new ClosedFileRowsIdle();
    }

    internal async ValueTask<ProjectPrefixErasure> SweepAsync(
        Ulid project, Ulid organization, IReadOnlyList<string> regions, CancellationToken cancellationToken)
    {
        var deletedAny = false;
        PlatformUnavailable? failure = null;
        foreach (var region in regions)
        {
            var erased = await objects.EraseProjectAsync(region, organization, project, cancellationToken);
            var step = erased switch
            {
                ProjectPrefixErased done => new RegionSweep(done.DeletedAny, null),
                PlatformUnavailable unavailable => new RegionSweep(false, unavailable),
            };
            deletedAny |= step.DeletedAny;
            failure = step.Failure ?? failure;
        }

        return failure is null ? new ProjectPrefixErased(deletedAny) : failure;
    }

    internal static IReadOnlyList<string> Regions(SqlHttpStatementResult result) => [.. result.RowsOf().Select(static row => row.Text(0))];

    public async ValueTask<EraseProjectFilesOutcome> EraseFilesAsync(
        Ulid project, Ulid organization, IReadOnlyList<string> regions, CancellationToken cancellationToken)
    {
        for (var round = 0; round < CloseRounds; round++)
        {
            var progress = await EraseRoundAsync(project.ToString(), organization.ToString(), cancellationToken);
            if (progress.Failure is { } failure)
            {
                return failure;
            }

            if (!progress.More)
            {
                break;
            }
        }

        return ToEraseOutcome(await SweepAsync(project, organization, regions, cancellationToken));
    }

    private async ValueTask<EraseProgress> EraseRoundAsync(string project, string organization, CancellationToken cancellationToken)
    {
        var projectId = SqlValues.Text(project);
        var organizationId = SqlValues.Text(organization);
        var more = false;
        foreach (var step in EraseSteps)
        {
            var outcome = await store.ExecuteAsync(
                [SqlValues.Statement(step, projectId, projectId, projectId, organizationId)], SqlContext.Fast, cancellationToken);
            var progress = outcome switch
            {
                SqlExecuteResponse { Results: var results } => new EraseProgress(results[0].RowsAffected > 0, null),
                PlatformConflict conflict => new EraseProgress(false, new PlatformUnavailable(conflict.Message, "platform_state")),
                PlatformUnavailable unavailable => new EraseProgress(false, unavailable),
            };
            if (progress.Failure is not null)
            {
                return progress;
            }

            more |= progress.More;
        }

        return new EraseProgress(more, null);
    }

    private static EraseProjectFilesOutcome ToEraseOutcome(ProjectPrefixErasure erased) => erased switch
    {
        ProjectPrefixErased => new ProjectClosed(),
        PlatformUnavailable unavailable => unavailable,
    };

    private readonly record struct EraseProgress(bool More, PlatformUnavailable? Failure);

    private readonly record struct RegionSweep(bool DeletedAny, PlatformUnavailable? Failure);

    private readonly record struct SweepTarget(SqlRow? Row, PlatformUnavailable? Failure);
}
