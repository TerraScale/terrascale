using System.Globalization;
using TerraBase.Configuration;
using TerraBase.Features.Catalog;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Qos;
using TerraBase.Features.Rules;
using TerraBase.Hosting;
using TerraBase.Storage;
using TerraScale.Contracts.Cluster;
using TerraScale.Contracts.Http;

namespace TerraBase.Features.Sql;

internal readonly union SqlCausalityOutcome(SqlCausality, SqlRefusal);

internal readonly union SqlRunOutcome(SqlRun, SqlRefusal);

internal readonly union SqlWaitOutcome(Completed, SqlRefusal);

public sealed record SqlRun(IReadOnlyList<SqlResultSet> Results, CausalBookmark Bookmark, ConsistencyMode Mode, string? RulesVersion = null);

public sealed record SqlCausality(ConsistencyMode? Mode, CausalBookmark After);

internal sealed class SqlRunner(DatabaseAccess access, IClusterCoordinator coordinator, RulesEnforcer rules, UsageMeter usage, QosAdmission qos, TerraBaseOptions options)
{
    public static SqlCausalityOutcome Causality(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var after = CausalBookmark.Empty;
        if (request.Headers.TryGetValue(ConsistencyHeaders.Bookmark, out var bookmark)
            && (bookmark is not [{ } encoded] || !CausalBookmark.TryDecode(encoded, out after)))
        {
            return new SqlRefusal("invalid_bookmark", "TerraScale-Bookmark must be one bookmark returned by TerraScale", "22023", 0, StatusCodes.Status400BadRequest);
        }

        ConsistencyMode? mode = null;
        if (request.Headers.TryGetValue(ConsistencyHeaders.Consistency, out var consistency))
        {
            if (consistency is not [{ } value] || !ConsistencyHeaders.TryParse(value, out var parsed))
            {
                return new SqlRefusal("invalid_consistency", "TerraScale-Consistency is fast or strong", "22023", 0, StatusCodes.Status400BadRequest);
            }

            mode = parsed;
        }

        return new SqlCausality(mode, after);
    }

    public static void WriteHeaders(HttpResponse response, SqlRun run)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(run);
        response.Headers[ConsistencyHeaders.Bookmark] = run.Bookmark.Encode();
        response.Headers[ConsistencyHeaders.Consistency] = ConsistencyHeaders.Format(run.Mode);
        if (run.RulesVersion is { Length: > 0 } rulesVersion)
        {
            response.Headers[PlatformHeaders.RulesVersion] = rulesVersion;
        }

        response.Headers.CacheControl = "no-store";
    }

    public ValueTask<SqlRunOutcome> RunAsync(
        ResolvedDatabase target, IReadOnlyList<SqlStatement> statements, SqlCausality causality, CancellationToken cancellationToken) =>
        RunAsync(target, statements, causality, serviceRead: false, cancellationToken);

    public async ValueTask<SqlRunOutcome> RunAsync(
        ResolvedDatabase target, IReadOnlyList<SqlStatement> statements, SqlCausality causality, bool serviceRead, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(causality);
        using var lease = await qos.AdmitAsync(target.Database.Id, cancellationToken);
        if (!lease.IsAcquired)
        {
            return new SqlRefusal("tenant_busy", "the database has too many requests in flight; retry", "53300", 0, StatusCodes.Status429TooManyRequests, TimeSpan.FromSeconds(1));
        }

        return SqlPlan.Batch(statements, allowTransactionControl: false) switch
        {
            SqlBatch batch => await RunBatchAsync(target, batch, causality, serviceRead, cancellationToken),
            SqlRefusal refusal => refusal,
        };
    }

    public async ValueTask<EnforcementOutcome> PrepareAsync(
        ResolvedDatabase target, IReadOnlyList<PlannedStatement> statements, CatalogReader catalog, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(statements);
        var firstWrite = FirstWrite(statements);
        if (firstWrite >= 0 && !target.CanWrite)
        {
            return SqlRefusal.ForbiddenScope(firstWrite);
        }

        return target.EndUser is { } auth
            ? await rules.ApplyAsync(target.Database.Id, auth, statements, cancellationToken, catalog)
            : new EnforcedSql([.. statements.Select(static statement => statement.Statement)], null);
    }

    public async ValueTask<SqlWaitOutcome> WaitAsync(ResolvedDatabase target, CausalBookmark after, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(after);
        if (after.IsEmpty)
        {
            return new Completed();
        }

        var outcome = await coordinator.WaitForBookmarkAsync(target.Database.Id, after, options.QueryBudget, cancellationToken);
        return outcome switch
        {
            BookmarkReached or DatabaseNotFound => new Completed(),
            ClusterTimeout timeout => SqlRefusal.From(timeout),
        };
    }

    public void Meter(ResolvedDatabase target, IReadOnlyList<SqlResultSet> results, bool wrote, ConsistencyMode mode)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(results);
        usage.Record(target.Project.Id, target.Database.Id, UsageMetrics.Requests, 1);
        usage.Record(target.Project.Id, target.Database.Id, wrote ? UsageMetrics.Writes : UsageMetrics.Reads, 1);
        usage.Record(target.Project.Id, target.Database.Id, UsageMetrics.RowsRead, results.Sum(static result => (long)result.Rows.Count));
        usage.Record(target.Project.Id, target.Database.Id, UsageMetrics.RowsWritten, wrote ? results.Sum(static result => result.RowsAffected) : 0);
        usage.Record(target.Project.Id, target.Database.Id, UsageMetrics.StrongCommits, wrote && mode == ConsistencyMode.Strong ? 1 : 0);
    }

    public static string Describe(CommitMetadata commit) =>
        string.Concat(commit.OriginRegion, ":", commit.Sequence.ToString(CultureInfo.InvariantCulture));

    private async ValueTask<SqlRunOutcome> RunBatchAsync(
        ResolvedDatabase target, SqlBatch batch, SqlCausality causality, bool serviceRead, CancellationToken cancellationToken)
    {
        var firstWrite = FirstWrite(batch.Statements);
        if (firstWrite >= 0 && !target.CanWrite)
        {
            return SqlRefusal.ForbiddenScope(firstWrite);
        }

        var mode = batch.Mode ?? causality.Mode ?? target.Database.DefaultConsistency;
        var waited = await WaitAsync(target, causality.After, cancellationToken);
        return waited switch
        {
            Completed => await access.OpenAsync(target.Database, cancellationToken) switch
            {
                ISqlDatabaseSession session => await PrepareAsync(target, batch.Statements, RulesEnforcer.Catalog(session), cancellationToken) switch
                {
                    EnforcedSql enforced => await ExecuteAsync(target, session, enforced.Statements, batch.Writes, mode, causality.After, enforced.RulesVersion, serviceRead, cancellationToken),
                    SqlRefusal refusal => refusal,
                },
                BackendUnavailable unavailable => SqlRefusal.From(unavailable),
            },
            SqlRefusal refusal => refusal,
        };
    }

    private async ValueTask<SqlRunOutcome> ExecuteAsync(
        ResolvedDatabase target, ISqlDatabaseSession session, IReadOnlyList<SqlStatement> statements, bool writes, ConsistencyMode mode, CausalBookmark after,
        string? rulesVersion, bool serviceRead, CancellationToken cancellationToken)
    {
        var request = new SqlStatementRequest(target.Database.Id, statements, writes ? SqlAccess.Write : SqlAccess.Read, mode, after);
        if (!writes)
        {
            var result = serviceRead
                ? await SqliteStorageEngine.ExecuteServiceReadAsync(session, request, cancellationToken)
                : await session.ExecuteReadAsync(request, cancellationToken);
            return result switch
            {
                SqlReadResult read => Finish(target, read.ResultSets, after.Merge(read.Observed), mode, wrote: false, rulesVersion),
                SqlError error => SqlRefusal.From(error),
                StorageBusy busy => SqlRefusal.From(busy),
                StorageTimeout timeout => SqlRefusal.From(timeout),
            };
        }

        var outcome = await coordinator.ExecuteWriteAsync(session, request, cancellationToken);
        return outcome switch
        {
            SqlWriteResult written => Finish(target, written.ResultSets, after.Merge(written.Commit.Bookmark).Merge(session.CurrentBookmark), written.Commit.Mode, wrote: true, rulesVersion),
            SqlError error => SqlRefusal.From(error),
            SerializationConflict conflict => SqlRefusal.From(conflict),
            SchemaMismatch mismatch => SqlRefusal.From(mismatch),
            ClusterUnavailable unavailable => SqlRefusal.From(unavailable),
            ClusterTimeout timeout => SqlRefusal.From(timeout),
            StorageBusy busy => SqlRefusal.From(busy),
        };
    }

    private SqlRun Finish(ResolvedDatabase target, IReadOnlyList<SqlResultSet> results, CausalBookmark bookmark, ConsistencyMode mode, bool wrote, string? rulesVersion)
    {
        Meter(target, results, wrote, mode);
        return new SqlRun(results, bookmark, mode, rulesVersion);
    }

    private static int FirstWrite(IReadOnlyList<PlannedStatement> statements)
    {
        for (var index = 0; index < statements.Count; index++)
        {
            if (statements[index].Kind is StatementKind.Write or StatementKind.Schema)
            {
                return index;
            }
        }

        return -1;
    }
}
