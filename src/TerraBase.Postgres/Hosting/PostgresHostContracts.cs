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
    CancellationToken Revocation,
    CancellationToken Shutdown = default);

public sealed record PostgresPasswordRejected(string Message);

public sealed record PostgresAccessDenied(string Message);

public sealed record PostgresSessionLimitReached(string Message);

public sealed record PostgresBackendUnavailable(string Message);

public readonly union PostgresAuthenticateOutcome(
    PostgresGrant,
    PostgresPasswordRejected,
    PostgresAccessDenied,
    PostgresSessionLimitReached,
    PostgresBackendUnavailable);

public interface IPostgresSessionFactory
{
    ValueTask<PostgresAuthenticateOutcome> AuthenticateAsync(
        PostgresStartup startup,
        CancellationToken cancellationToken);
}

public interface IPostgresDatabaseExecutor
{
    Ulid DatabaseId { get; }

    ulong? SchemaEpoch => null;

    CausalBookmark CurrentBookmark { get; }

    ValueTask<SqlReadOutcome> ReadAsync(
        SqlStatementRequest request,
        CancellationToken cancellationToken);

    ValueTask<ClusterWriteOutcome> WriteAsync(
        SqlStatementRequest request,
        CancellationToken cancellationToken);

    ValueTask<ClusterBeginTransactionOutcome> BeginTransactionAsync(
        SqlTransactionOptions options,
        CancellationToken cancellationToken);

    ValueTask<ClusterCommitTransactionOutcome> CommitAsync(
        ISqlTransaction transaction,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ChangefeedEntry> SubscribeChangesAsync(CausalBookmark after, CancellationToken cancellationToken);
}
