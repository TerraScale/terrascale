using TerraBase.Features.Catalog;
using TerraBase.Features.Rules;
using TerraBase.Features.Sql;
using TerraBase.Hosting;
using TerraBase.Postgres.Hosting;
using TerraScale.Contracts.Cluster;
using TerraScale.Contracts.Continuity;

namespace TerraBase.Features.PostgresHost;

internal readonly union PostgresRequestPreparation(SqlStatementRequest, SqlError);

internal readonly union PostgresSessionOutcome(ISqlDatabaseSession, StorageBusy);

internal sealed class TerraBaseDatabaseExecutor(
    ResolvedDatabase target, DatabaseAccess access, ISqlDatabaseSession session, IClusterCoordinator coordinator, RulesEnforcer rules, SqlRunner runner) : IPostgresDatabaseExecutor
{
    private static readonly TimeSpan ReopenRetryAfter = TimeSpan.FromSeconds(1);

    private ISqlDatabaseSession _session = session;

    public Ulid DatabaseId => target.Database.Id;

    public ulong? SchemaEpoch => Volatile.Read(ref _session).SchemaEpoch;

    public CausalBookmark CurrentBookmark => coordinator.CurrentBookmark(target.Database.Id).Merge(Volatile.Read(ref _session).CurrentBookmark);

    public async ValueTask<SqlReadOutcome> ReadAsync(SqlStatementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resolved = await ResolveSessionAsync(cancellationToken);
        return resolved switch
        {
            ISqlDatabaseSession current => await ReadWithAsync(current, request, cancellationToken),
            StorageBusy busy => busy,
        };
    }

    public async ValueTask<ClusterWriteOutcome> WriteAsync(SqlStatementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resolved = await ResolveSessionAsync(cancellationToken);
        return resolved switch
        {
            ISqlDatabaseSession current => await WriteWithAsync(current, request, cancellationToken),
            StorageBusy busy => busy,
        };
    }

    public async ValueTask<ClusterBeginTransactionOutcome> BeginTransactionAsync(SqlTransactionOptions options, CancellationToken cancellationToken)
    {
        var resolved = await ResolveSessionAsync(cancellationToken);
        return resolved switch
        {
            ISqlDatabaseSession current => await BeginWithAsync(current, options, cancellationToken),
            StorageBusy busy => busy,
        };
    }

    public ValueTask<ClusterCommitTransactionOutcome> CommitAsync(ISqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return coordinator.CommitTransactionAsync(
            Volatile.Read(ref _session),
            transaction is RulesTransaction wrapped ? wrapped.Inner : transaction,
            cancellationToken);
    }

    public async ValueTask<StorageBusy?> EnsureUsableAsync(CancellationToken cancellationToken)
    {
        var resolved = await ResolveSessionAsync(cancellationToken);
        return resolved switch
        {
            ISqlDatabaseSession => null,
            StorageBusy busy => busy,
        };
    }

    public IAsyncEnumerable<ChangefeedEntry> SubscribeChangesAsync(CausalBookmark after, CancellationToken cancellationToken) =>
        Volatile.Read(ref _session).SubscribeChangesAsync(after, cancellationToken);

    private async ValueTask<PostgresSessionOutcome> ResolveSessionAsync(CancellationToken cancellationToken)
    {
        var opened = await access.ReopenAsync(target.Database, cancellationToken);
        return opened switch
        {
            ISqlDatabaseSession current => Track(current),
            ResourceNotFound missing => new StorageBusy(missing.Message, ReopenRetryAfter),
            BackendUnavailable unavailable => new StorageBusy(unavailable.Message, ReopenRetryAfter),
        };
    }

    private PostgresSessionOutcome Track(ISqlDatabaseSession current)
    {
        Volatile.Write(ref _session, current);
        return new PostgresSessionOutcome(current);
    }

    private async ValueTask<SqlReadOutcome> ReadWithAsync(ISqlDatabaseSession current, SqlStatementRequest request, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(current, request, cancellationToken);
        return prepared switch
        {
            SqlStatementRequest ready => await ReadPreparedAsync(current, ready, cancellationToken),
            SqlError error => error,
        };
    }

    private async ValueTask<ClusterWriteOutcome> WriteWithAsync(ISqlDatabaseSession current, SqlStatementRequest request, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(current, request, cancellationToken);
        return prepared switch
        {
            SqlStatementRequest ready => await WritePreparedAsync(current, ready, cancellationToken),
            SqlError error => error,
        };
    }

    private async ValueTask<ClusterBeginTransactionOutcome> BeginWithAsync(ISqlDatabaseSession current, SqlTransactionOptions options, CancellationToken cancellationToken)
    {
        var begun = await coordinator.BeginTransactionAsync(current, options, cancellationToken);
        return begun switch
        {
            ISqlTransaction transaction => Transaction(target.EndUser is { } auth ? new RulesTransaction(transaction, target.Database.Id, auth, rules) : transaction),
            StorageBusy busy => busy,
            ClusterTimeout timeout => timeout,
        };
    }

    private static ClusterBeginTransactionOutcome Transaction(ISqlTransaction transaction) => new(transaction);

    private async ValueTask<SqlReadOutcome> ReadPreparedAsync(ISqlDatabaseSession current, SqlStatementRequest request, CancellationToken cancellationToken)
    {
        var read = await current.ExecuteReadAsync(request, cancellationToken);
        return read switch
        {
            SqlReadResult result => Metered(result),
            SqlError error => error,
            StorageBusy busy => busy,
            StorageTimeout timeout => timeout,
        };
    }

    private async ValueTask<ClusterWriteOutcome> WritePreparedAsync(ISqlDatabaseSession current, SqlStatementRequest request, CancellationToken cancellationToken)
    {
        var written = await coordinator.ExecuteWriteAsync(current, request, cancellationToken);
        return written switch
        {
            SqlWriteResult result => Metered(result),
            SqlError error => error,
            SerializationConflict conflict => conflict,
            SchemaMismatch mismatch => mismatch,
            ClusterUnavailable unavailable => unavailable,
            ClusterTimeout timeout => timeout,
            StorageBusy busy => busy,
        };
    }

    private SqlReadResult Metered(SqlReadResult read)
    {
        runner.Meter(target, read.ResultSets, wrote: false, ConsistencyMode.Fast);
        return read;
    }

    private SqlWriteResult Metered(SqlWriteResult written)
    {
        runner.Meter(target, written.ResultSets, wrote: true, written.Commit.Mode);
        return written;
    }

    private async ValueTask<PostgresRequestPreparation> PrepareAsync(ISqlDatabaseSession current, SqlStatementRequest request, CancellationToken cancellationToken)
    {
        if (target.EndUser is not { } auth)
        {
            return request;
        }

        List<PlannedStatement> planned = [];
        for (var index = 0; index < request.Statements.Count; index++)
        {
            if (SqlPlan.Classify(request.Statements[index], index) switch { PlannedStatement statement => Add(planned, statement), SqlRefusal refusal => refusal } is { } failure)
            {
                return Error(failure);
            }
        }

        var enforced = await rules.ApplyAsync(target.Database.Id, auth, planned, current, cancellationToken);
        return enforced switch
        {
            EnforcedSql ready => request with { Statements = ready.Statements },
            SqlRefusal refusal => Error(refusal),
        };
    }

    public static SqlError Error(SqlRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        return new SqlError(refusal.Code, refusal.Message, refusal.SqlState, refusal.Statement, 0);
    }

    private static SqlRefusal? Add(List<PlannedStatement> planned, PlannedStatement statement)
    {
        planned.Add(statement);
        return null;
    }
}

internal sealed class RulesTransaction(ISqlTransaction inner, Ulid databaseId, RulesAuth auth, RulesEnforcer rules) : ISqlTransaction
{
    public ISqlTransaction Inner { get; } = inner;

    public Ulid TxId => Inner.TxId;

    public SqlTransactionOptions Options => Inner.Options;

    public async ValueTask<TransactionExecuteResult> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return SqlPlan.Classify(statement, 0) switch
        {
            PlannedStatement planned => await ExecutePlannedAsync(planned, cancellationToken),
            SqlRefusal refusal => TerraBaseDatabaseExecutor.Error(refusal),
        };
    }

    public ValueTask<SqlReadOutcome> ReadCatalogAsync(IReadOnlyList<SqlStatement> statements, CancellationToken cancellationToken) =>
        Inner.ReadCatalogAsync(statements, cancellationToken);

    public ValueTask<TransactionCommitLocalResult> CommitLocalAsync(CancellationToken cancellationToken) => Inner.CommitLocalAsync(cancellationToken);

    public ValueTask<TransactionStageResult> StageAsync(CancellationToken cancellationToken) => Inner.StageAsync(cancellationToken);

    public ValueTask RollbackAsync(CancellationToken cancellationToken) => Inner.RollbackAsync(cancellationToken);

    public ValueTask DisposeAsync() => Inner.DisposeAsync();

    private async ValueTask<TransactionExecuteResult> ExecutePlannedAsync(PlannedStatement planned, CancellationToken cancellationToken)
    {
        var enforced = await rules.ApplyAsync(databaseId, auth, [planned], cancellationToken, RulesEnforcer.Catalog(Inner));
        return enforced switch
        {
            EnforcedSql { Statements: [var rewritten] } => await Inner.ExecuteAsync(rewritten, cancellationToken),
            EnforcedSql => TerraBaseDatabaseExecutor.Error(SqlRefusal.RuleDenied("the statement could not be checked against the rules", 0)),
            SqlRefusal refusal => TerraBaseDatabaseExecutor.Error(refusal),
        };
    }
}
