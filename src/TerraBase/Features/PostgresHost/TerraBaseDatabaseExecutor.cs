using TerraBase.Features.Catalog;
using TerraBase.Features.Rules;
using TerraBase.Features.Sql;
using TerraBase.Postgres.Hosting;
using TerraScale.Contracts.Cluster;
using TerraScale.Contracts.Continuity;

namespace TerraBase.Features.PostgresHost;

internal sealed class TerraBaseDatabaseExecutor(
    ResolvedDatabase target, ISqlDatabaseSession session, IClusterCoordinator coordinator, RulesEnforcer rules, SqlRunner runner) : IPostgresDatabaseExecutor
{
    public Ulid DatabaseId => session.DatabaseId;

    public CausalBookmark CurrentBookmark => coordinator.CurrentBookmark(session.DatabaseId).Merge(session.CurrentBookmark);

    public async ValueTask<Result<SqlReadResult, SqlError, StorageBusy, RequestTimeout>> ReadAsync(SqlStatementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await PrepareAsync(request, cancellationToken) switch
        {
            SqlStatementRequest prepared => await session.ExecuteReadAsync(prepared, cancellationToken) switch
            {
                SqlReadResult read => Metered(read),
                SqlError error => error,
                StorageBusy busy => busy,
                RequestTimeout timeout => timeout,
            },
            SqlError error => error,
        };
    }

    public async ValueTask<Result<SqlWriteResult, SqlError, SerializationConflict, SchemaMismatch, Unavailable, RequestTimeout, StorageBusy>> WriteAsync(
        SqlStatementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await PrepareAsync(request, cancellationToken) switch
        {
            SqlStatementRequest prepared => await coordinator.ExecuteWriteAsync(session, prepared, cancellationToken) switch
            {
                SqlWriteResult written => Metered(written),
                SqlError error => error,
                SerializationConflict conflict => conflict,
                SchemaMismatch mismatch => mismatch,
                Unavailable unavailable => unavailable,
                RequestTimeout timeout => timeout,
                StorageBusy busy => busy,
            },
            SqlError error => error,
        };
    }

    public async ValueTask<Result<ISqlTransaction, StorageBusy, RequestTimeout>> BeginTransactionAsync(SqlTransactionOptions options, CancellationToken cancellationToken) =>
        await coordinator.BeginTransactionAsync(session, options, cancellationToken) switch
        {
            ISqlTransaction transaction => Transaction(target.EndUser is { } auth ? new RulesTransaction(transaction, target.Database.Id, auth, rules) : transaction),
            StorageBusy busy => busy,
            RequestTimeout timeout => timeout,
        };

    public ValueTask<Result<CommitMetadata, SerializationConflict, SchemaMismatch, Unavailable, RequestTimeout, StorageBusy>> CommitAsync(
        ISqlTransaction transaction, CancellationToken cancellationToken) =>
        coordinator.CommitTransactionAsync(session, transaction is RulesTransaction wrapped ? wrapped.Inner : transaction, cancellationToken);

    public IAsyncEnumerable<ChangefeedEntry> SubscribeChangesAsync(CausalBookmark after, CancellationToken cancellationToken) =>
        session.SubscribeChangesAsync(after, cancellationToken);

    private static Result<ISqlTransaction, StorageBusy, RequestTimeout> Transaction(ISqlTransaction transaction) => new(transaction);

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

    private async ValueTask<Result<SqlStatementRequest, SqlError>> PrepareAsync(SqlStatementRequest request, CancellationToken cancellationToken)
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

        return await rules.ApplyAsync(target.Database.Id, auth, planned, cancellationToken) switch
        {
            IReadOnlyList<SqlStatement> statements => request with { Statements = statements },
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

    public async ValueTask<Result<SqlResultSet, SqlError, StorageBusy, RequestTimeout>> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return SqlPlan.Classify(statement, 0) switch
        {
            PlannedStatement planned => await rules.ApplyAsync(databaseId, auth, [planned], cancellationToken) switch
            {
                IReadOnlyList<SqlStatement> { Count: 1 } rewritten => await Inner.ExecuteAsync(rewritten[0], cancellationToken),
                IReadOnlyList<SqlStatement> => TerraBaseDatabaseExecutor.Error(SqlRefusal.RuleDenied("the statement could not be checked against the rules", 0)),
                SqlRefusal refusal => TerraBaseDatabaseExecutor.Error(refusal),
            },
            SqlRefusal refusal => TerraBaseDatabaseExecutor.Error(refusal),
        };
    }

    public ValueTask<Result<CommitMetadata, StrongRequired, SchemaMismatch, StorageBusy, RequestTimeout>> CommitLocalAsync(CancellationToken cancellationToken) =>
        Inner.CommitLocalAsync(cancellationToken);

    public ValueTask<Result<StagedWrite, SchemaMismatch, StorageBusy, RequestTimeout>> StageAsync(CancellationToken cancellationToken) => Inner.StageAsync(cancellationToken);

    public ValueTask<Done> RollbackAsync(CancellationToken cancellationToken) => Inner.RollbackAsync(cancellationToken);

    public ValueTask DisposeAsync() => Inner.DisposeAsync();
}
