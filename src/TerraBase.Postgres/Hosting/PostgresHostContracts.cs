using System.Net;
using TerraScale.Contracts.Cluster;
using TerraScale.Contracts.Consistency;
using TerraScale.Contracts.Continuity;

namespace TerraBase.Postgres.Hosting;

public sealed record PostgresStartup(
    string User,
    string ProjectReference,
    string DatabaseReference,
    string Password,
    string ApplicationName,
    IPAddress? ClientAddress,
    IReadOnlyDictionary<string, string> Parameters);

public sealed record PostgresGrant(
    IPostgresDatabaseExecutor Executor,
    string DatabaseName,
    SqlAccess MaxAccess,
    IAsyncDisposable Lease,
    CancellationToken Revocation);

public interface IPostgresSessionFactory
{
    ValueTask<Result<PostgresGrant, Unauthorized, Forbidden, QuotaExceeded, Unavailable>> AuthenticateAsync(
        PostgresStartup startup,
        CancellationToken cancellationToken);
}

public interface IPostgresDatabaseExecutor
{
    Ulid DatabaseId { get; }

    CausalBookmark CurrentBookmark { get; }

    ValueTask<Result<SqlReadResult, SqlError, StorageBusy, RequestTimeout>> ReadAsync(
        SqlStatementRequest request,
        CancellationToken cancellationToken);

    ValueTask<Result<SqlWriteResult, SqlError, SerializationConflict, SchemaMismatch, Unavailable, RequestTimeout, StorageBusy>> WriteAsync(
        SqlStatementRequest request,
        CancellationToken cancellationToken);

    ValueTask<Result<ISqlTransaction, StorageBusy, RequestTimeout>> BeginTransactionAsync(
        SqlTransactionOptions options,
        CancellationToken cancellationToken);

    ValueTask<Result<CommitMetadata, SerializationConflict, SchemaMismatch, Unavailable, RequestTimeout, StorageBusy>> CommitAsync(
        ISqlTransaction transaction,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ChangefeedEntry> SubscribeChangesAsync(CausalBookmark after, CancellationToken cancellationToken);
}
