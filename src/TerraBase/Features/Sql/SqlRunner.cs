using System.Globalization;
using System.Runtime.CompilerServices;
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

public sealed record SqlCausality(ConsistencyMode? Mode, CausalBookmark After)
{
    public static SqlCausality Default { get; } = new(null, CausalBookmark.Empty);
}

internal sealed class SqlRunner(DatabaseAccess access, IClusterCoordinator coordinator, RulesEnforcer rules, UsageMeter usage, QosAdmission qos, TerraBaseOptions options)
{
    private static readonly Completed Done = new();

    private static readonly ConditionalWeakTable<CausalBookmark, string> EncodedBookmarks = new();

    private static BookmarkEncoding? _lastEncodedBookmark;

    public static string EncodeBookmark(CausalBookmark bookmark)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        if (bookmark.IsEmpty)
        {
            return string.Empty;
        }

        if (Volatile.Read(ref _lastEncodedBookmark) is { } cached && ReferenceEquals(cached.Bookmark, bookmark))
        {
            return cached.Encoded;
        }

        var encoded = EncodedBookmarks.GetValue(bookmark, static value => value.Encode());
        Volatile.Write(ref _lastEncodedBookmark, new BookmarkEncoding(bookmark, encoded));
        return encoded;
    }

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

        return mode is null && after.IsEmpty ? SqlCausality.Default : new SqlCausality(mode, after);
    }

    public static void WriteHeaders(HttpResponse response, SqlRun run)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(run);
        response.Headers[ConsistencyHeaders.Bookmark] = EncodeBookmark(run.Bookmark);
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

        if (statements is [var single]
            && target.EndUser is null
            && SqlPlan.TryClassifyCached(single.Sql, out var kind)
            && kind is not (StatementKind.Begin or StatementKind.Commit or StatementKind.Rollback))
        {
            var writes = kind is StatementKind.Write or StatementKind.Schema;
            if (writes && !target.CanWrite)
            {
                return SqlRefusal.ForbiddenScope(0);
            }

            var mode = causality.Mode ?? target.Database.DefaultConsistency;
            return await RunDirectAsync(target, statements, writes, mode, causality.After, serviceRead, cancellationToken);
        }

        return SqlPlan.Batch(statements, allowTransactionControl: false) switch
        {
            SqlBatch batch => await RunBatchAsync(target, batch, causality, serviceRead, cancellationToken),
            SqlRefusal refusal => refusal,
        };
    }

    public async ValueTask<SqlRunOutcome> RunSingleAutocommitAsync(
        ResolvedDatabase target, IReadOnlyList<SqlStatement> statements, StatementKind kind, ConsistencyMode mode, CausalBookmark after, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(after);
        using var lease = await qos.AdmitAsync(target.Database.Id, cancellationToken);
        if (!lease.IsAcquired)
        {
            return new SqlRefusal("tenant_busy", "the database has too many requests in flight; retry", "53300", 0, StatusCodes.Status429TooManyRequests, TimeSpan.FromSeconds(1));
        }

        var writes = kind is StatementKind.Write or StatementKind.Schema;
        if (writes && !target.CanWrite)
        {
            return SqlRefusal.ForbiddenScope(0);
        }

        return await RunDirectAsync(target, statements, writes, mode, after, serviceRead: false, cancellationToken);
    }

    public async ValueTask<SqlRunOutcome> RunPlannedAsync(
        ResolvedDatabase target, SqlBatch batch, SqlCausality causality, bool serviceRead, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(causality);
        using var lease = await qos.AdmitAsync(target.Database.Id, cancellationToken);
        if (!lease.IsAcquired)
        {
            return new SqlRefusal("tenant_busy", "the database has too many requests in flight; retry", "53300", 0, StatusCodes.Status429TooManyRequests, TimeSpan.FromSeconds(1));
        }

        return await RunBatchAsync(target, batch, causality, serviceRead, cancellationToken);
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
            : new EnforcedSql(ExtractStatements(statements), null);
    }

    public ValueTask<EnforcementOutcome> PrepareBatchAsync(
        ResolvedDatabase target, SqlBatch batch, ISqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(batch);
        var firstWrite = FirstWrite(batch.Statements);
        if (firstWrite >= 0 && !target.CanWrite)
        {
            return ValueTask.FromResult<EnforcementOutcome>(SqlRefusal.ForbiddenScope(firstWrite));
        }

        return target.EndUser is { } auth
            ? rules.ApplyAsync(target.Database.Id, auth, batch.Statements, cancellationToken, RulesEnforcer.Catalog(transaction))
            : ValueTask.FromResult<EnforcementOutcome>(new EnforcedSql(batch.Sql, null));
    }

    public ValueTask<SqlWaitOutcome> WaitAsync(ResolvedDatabase target, CausalBookmark after, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(after);
        if (after.IsEmpty)
        {
            return ValueTask.FromResult<SqlWaitOutcome>(Done);
        }

        return WaitSlowAsync(target.Database.Id, after, cancellationToken);
    }

    private async ValueTask<SqlWaitOutcome> WaitSlowAsync(Ulid databaseId, CausalBookmark after, CancellationToken cancellationToken)
    {
        var outcome = await coordinator.WaitForBookmarkAsync(databaseId, after, options.QueryBudget, cancellationToken);
        return outcome switch
        {
            BookmarkReached or DatabaseNotFound => Done,
            ClusterTimeout timeout => SqlRefusal.From(timeout),
        };
    }

    public void Meter(ResolvedDatabase target, IReadOnlyList<SqlResultSet> results, bool wrote, ConsistencyMode mode)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(results);
        long rowsRead = 0;
        long rowsWritten = 0;
        for (var index = 0; index < results.Count; index++)
        {
            var result = results[index];
            rowsRead += result.Rows.Count;
            if (wrote)
            {
                rowsWritten += result.RowsAffected;
            }
        }

        usage.RecordSqlBatch(
            target.Project.Id,
            target.Database.Id,
            requests: 1,
            reads: wrote ? 0 : 1,
            writes: wrote ? 1 : 0,
            rowsRead: rowsRead,
            rowsWritten: rowsWritten,
            strongCommits: wrote && mode == ConsistencyMode.Strong ? 1 : 0);
    }

    public static string Describe(CommitMetadata commit) =>
        string.Concat(commit.OriginRegion, ":", commit.Sequence.ToString(CultureInfo.InvariantCulture));

    private async ValueTask<SqlRunOutcome> RunDirectAsync(
        ResolvedDatabase target, IReadOnlyList<SqlStatement> statements, bool writes, ConsistencyMode mode, CausalBookmark after, bool serviceRead, CancellationToken cancellationToken)
    {
        var waited = await WaitAsync(target, after, cancellationToken);
        return waited switch
        {
            Completed => await access.OpenAsync(target.Database, cancellationToken) switch
            {
                ISqlDatabaseSession session => await ExecuteAsync(target, session, statements, writes, mode, after, null, serviceRead, cancellationToken),
                BackendUnavailable unavailable => SqlRefusal.From(unavailable),
            },
            SqlRefusal refusal => refusal,
        };
    }

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
                ISqlDatabaseSession session when target.EndUser is null =>
                    await ExecuteAsync(target, session, batch.Sql, batch.Writes, mode, causality.After, null, serviceRead, cancellationToken),
                ISqlDatabaseSession session => await rules.ApplyAsync(target.Database.Id, target.EndUser, batch.Statements, cancellationToken, RulesEnforcer.Catalog(session)) switch
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
                SqlReadResult read => Finish(target, read.ResultSets, after.IsEmpty ? read.Observed : after.Merge(read.Observed), mode, wrote: false, rulesVersion),
                SqlError error => SqlRefusal.From(error),
                StorageBusy busy => SqlRefusal.From(busy),
                StorageTimeout timeout => SqlRefusal.From(timeout),
            };
        }

        var outcome = await coordinator.ExecuteWriteAsync(session, request, cancellationToken);
        return outcome switch
        {
            SqlWriteResult written => Finish(target, written.ResultSets, MergeWriteBookmark(after, written.Commit.Bookmark, session.CurrentBookmark), written.Commit.Mode, wrote: true, rulesVersion),
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

    private static CausalBookmark MergeWriteBookmark(CausalBookmark after, CausalBookmark committed, CausalBookmark current)
    {
        var merged = after.IsEmpty ? committed : after.Merge(committed);
        return ReferenceEquals(merged, current) || current.IsEmpty ? merged : merged.Merge(current);
    }

    private static SqlStatement[] ExtractStatements(IReadOnlyList<PlannedStatement> statements)
    {
        if (statements.Count == 0)
        {
            return [];
        }

        var extracted = new SqlStatement[statements.Count];
        for (var index = 0; index < statements.Count; index++)
        {
            extracted[index] = statements[index].Statement;
        }

        return extracted;
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

    private sealed record BookmarkEncoding(CausalBookmark Bookmark, string Encoded);
}
