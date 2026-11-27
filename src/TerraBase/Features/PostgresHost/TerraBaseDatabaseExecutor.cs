using TerraBase.Features.Catalog;
using TerraBase.Features.Rules;
using TerraBase.Features.Sql;
using TerraBase.Hosting;
using TerraBase.Postgres.Hosting;
using TerraScale.Contracts.Cluster;
using TerraScale.Contracts.Continuity;

namespace TerraBase.Features.PostgresHost;

internal readonly union PostgresRequestPreparation(SqlStatementRequest, SqlError);

internal sealed class TerraBaseDatabaseExecutor(
    ResolvedDatabase target, ISqlDatabaseSession session, IClusterCoordinator coordinator, RulesEnforcer rules, SqlRunner runner) : IPostgresDatabaseExecutor
{
    public Ulid DatabaseId => session.DatabaseId;

    public ulong? SchemaEpoch => session.SchemaEpoch;

    public CausalBookmark CurrentBookmark => coordinator.CurrentBookmark(session.DatabaseId).Merge(session.CurrentBookmark);

    public async ValueTask<SqlReadOutcome> ReadAsync(SqlStatementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepared = await PrepareAsync(request, cancellationToken);
        return prepared switch
        {
            SqlStatementRequest ready => await ReadPreparedAsync(ready, cancellationToken),
            SqlError error => error,
        };
    }

    public async ValueTask<ClusterWriteOutcome> WriteAsync(SqlStatementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepared = await PrepareAsync(request, cancellationToken);
        return prepared switch
        {
            SqlStatementRequest ready => await WritePreparedAsync(ready, cancellationToken),
            SqlError error => error,
        };
    }

    public async ValueTask<ClusterBeginTransactionOutcome> BeginTransactionAsync(SqlTransactionOptions options, CancellationToken cancellationToken)
    {
        var begun = await coordinator.BeginTransactionAsync(session, options, cancellationToken);
        return begun switch
        {
            ISqlTransaction transaction => Transaction(target.EndUser is { } auth ? new RulesTransaction(transaction, target.Database.Id, auth, rules) : transaction),
            StorageBusy busy => busy,
            ClusterTimeout timeout => timeout,
        };
    }

    public ValueTask<ClusterCommitTransactionOutcome> CommitAsync(ISqlTransaction transaction, CancellationToken cancellationToken) =>
        coordinator.CommitTransactionAsync(session, transaction is RulesTransaction wrapped ? wrapped.Inner : transaction, cancellationToken);

    public IAsyncEnumerable<ChangefeedEntry> SubscribeChangesAsync(CausalBookmark after, CancellationToken cancellationToken) =>
        session.SubscribeChangesAsync(after, cancellationToken);

    private static ClusterBeginTransactionOutcome Transaction(ISqlTransaction transaction) => new(transaction);

    private async ValueTask<SqlReadOutcome> ReadPreparedAsync(SqlStatementRequest request, CancellationToken cancellationToken)
    {
        var read = await session.ExecuteReadAsync(request, cancellationToken);
        return read switch
        {
            SqlReadResult result => Metered(result),
            SqlError error => error,
            StorageBusy busy => busy,
            StorageTimeout timeout => timeout,
        };
    }

    private async ValueTask<ClusterWriteOutcome> WritePreparedAsync(SqlStatementRequest request, CancellationToken cancellationToken)
    {
        var written = await coordinator.ExecuteWriteAsync(session, request, cancellationToken);
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

    private async ValueTask<PostgresRequestPreparation> PrepareAsync(SqlStatementRequest request, CancellationToken cancellationToken)
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

        var enforced = await rules.ApplyAsync(target.Database.Id, auth, planned, session, cancellationToken);
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
