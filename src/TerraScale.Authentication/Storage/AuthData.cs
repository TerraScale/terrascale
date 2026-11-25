using System.Data.Common;
using System.Net;
using System.Net.WebSockets;
using TerraScale.Authentication.Contracts;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Storage;

public readonly union AuthTransaction<T>(T, AuthConflict);

internal sealed record AuthStorageDown(Exception Cause);

internal sealed record AuthContended(Exception Cause);

internal sealed record AuthOutcomeUnknown(Exception Cause);

internal readonly union AuthBatchWrite(AuthCompleted, AuthConflict, AuthContended, AuthStorageDown, AuthOutcomeUnknown);

public sealed class AuthData(AuthConnectionFactory connections)
{
    private const int ContentionRetries = 2;

    private const string SerializationFailureState = "40001";

    internal const string StorageBusy = "storage_busy";

    internal const string StrongQuorumUnreachable = "strong_quorum_unreachable";

    private const string InRegionQuorumUnconfirmed = "in_region_quorum_unconfirmed";

    private const string RequestTimeout = "request_timeout";

    private const string WriteOutcomeUnknown = "write_outcome_unknown";

    internal const string LostConflict = "A conditional write lost to a concurrent change.";

    private static readonly AsyncLocal<StorageWrites?> Tracked = new();

    private readonly AsyncLocal<WriteAudit?> audit = new();

    public static StorageWrites TrackWrites()
    {
        var writes = new StorageWrites();
        Tracked.Value = writes;
        return writes;
    }

    public async Task<T> AuditWritesAsync<T>(Change success, Func<CancellationToken, Task<T>> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(success);
        ArgumentNullException.ThrowIfNull(write);
        var previous = audit.Value;
        var current = new WriteAudit(success);
        audit.Value = current;
        try
        {
            var result = await write(cancellationToken);
            if (!current.Committed && !await SaveAsync([], cancellationToken))
            {
                throw ApiException.Unavailable();
            }

            return result;
        }
        finally
        {
            audit.Value = previous;
        }
    }

    public async Task<T> ReadAsync<T>(Func<DbConnection, Task<T>> read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await read(connection);
    }

    public async Task<UserRecord?> ReadUserAsync(Func<DbConnection, Task<UserRecord?>> read, CancellationToken cancellationToken) =>
        await ReadAsync(read, cancellationToken) is { } user ? Stored(user) : null;

    public async Task<IReadOnlyList<UserRecord>> ReadUsersAsync(Func<DbConnection, Task<IEnumerable<UserRecord>>> read, CancellationToken cancellationToken) =>
        [.. (await ReadAsync(read, cancellationToken)).Select(Stored)];

    private static UserRecord Stored(UserRecord user) => user with { LinkedProviders = [], AuthenticationBindings = [], StepUpBindings = [] };

    public async Task<int> InBatchesAsync(int batchSize, Func<DbConnection, Task<int>> statement, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentNullException.ThrowIfNull(statement);
        var total = 0;
        int changed;
        do
        {
            changed = await ReadAsync(statement, cancellationToken);
            total += changed;
        }
        while (changed >= batchSize);

        return total;
    }

    public Task<bool> SaveAsync(Change change, CancellationToken cancellationToken) => SaveAsync([change], cancellationToken);

    public async Task<bool> SaveAsync(IReadOnlyList<Change> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var currentAudit = audit.Value;
        if (changes.Count == 0 && currentAudit is not { Committed: false })
        {
            return true;
        }

        if (Plan(changes, currentAudit) is not { } batch)
        {
            return false;
        }

        var outcome = await WriteAsync(batch, cancellationToken);
        for (var retry = 0; retry < ContentionRetries && Contended(outcome); retry++)
        {
            outcome = await WriteAsync(batch, cancellationToken);
        }

        return outcome switch
        {
            AuthCompleted => Committed(currentAudit, true),
            AuthConflict => false,
            AuthContended contended => throw ApiException.Unavailable(contended.Cause),
            AuthStorageDown down => throw ApiException.Unavailable(down.Cause),
            AuthOutcomeUnknown unknown => throw OutcomeUnknown(unknown.Cause),
        };
    }

    public async Task<AuthTransaction<T>> TransactAsync<T>(Func<AuthTransaction, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var scope = new AuthTransaction(connection, transaction);
        var result = await work(scope);
        var currentAudit = audit.Value;
        if (Plan(scope.Staged, currentAudit) is not { } batch)
        {
            return new AuthConflict(LostConflict);
        }

        var executed = await ExecuteAsync(batch, connection, transaction, cancellationToken);
        var outcome = executed switch
        {
            AuthCompleted => await CommitAsync(transaction, cancellationToken),
            AuthConflict or AuthContended or AuthStorageDown or AuthOutcomeUnknown => executed,
        };
        return outcome switch
        {
            AuthCompleted => Committed(currentAudit, result),
            AuthConflict conflict => conflict,
            AuthContended contended => throw ApiException.Unavailable(contended.Cause),
            AuthStorageDown down => throw ApiException.Unavailable(down.Cause),
            AuthOutcomeUnknown unknown => throw OutcomeUnknown(unknown.Cause),
        };
    }

    public static bool IsContention(Exception? failure) =>
        failure is TerraBaseSqlException { Code: TerraBaseSqlErrorCodes.SerializationConflict, Sqlstate: SerializationFailureState };

    public static bool IsConflict(Exception failure) => failure is TerraBaseException
    {
        ErrorCode: TerraBaseSqlErrorCodes.UniqueViolation or TerraBaseSqlErrorCodes.PrimaryKeyViolation or TerraBaseSqlErrorCodes.ForeignKeyViolation
            or TerraBaseSqlErrorCodes.CheckViolation or TerraBaseSqlErrorCodes.RowConflict or TerraBaseSqlErrorCodes.SerializationConflict,
    };

    public static bool IsUnavailable(Exception failure) => IsOutcomeUnknown(failure) || failure switch
    {
        ApiException { Status: StatusCodes.Status503ServiceUnavailable } => true,
        TerraBaseException
        {
            ErrorCode: TerraBaseSqlErrorCodes.TenantBusy or TerraBaseSqlErrorCodes.Unavailable or TerraBaseSqlErrorCodes.TransactionRolledBack or StorageBusy
                or StrongQuorumUnreachable,
        } => true,
        TerraBaseException { StatusCode: HttpStatusCode.ServiceUnavailable } => true,
        TerraBaseUnavailableException or TimeoutException or WebSocketException => true,
        HttpRequestException { StatusCode: null } and not TerraBaseException => true,
        _ when failure.InnerException is { } inner => IsUnavailable(inner),
        _ => false,
    };

    public static bool IsOutcomeUnknown(Exception failure) => failure switch
    {
        ApiException => false,
        TerraBaseException { ErrorCode: WriteOutcomeUnknown or InRegionQuorumUnconfirmed or RequestTimeout or TerraBaseSqlErrorCodes.QueryCanceled } => true,
        TerraBaseException { StatusCode: HttpStatusCode.GatewayTimeout } => true,
        _ when failure.InnerException is { } inner => IsOutcomeUnknown(inner),
        _ => false,
    };

    public static ApiException Unavailable(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure switch
        {
            ApiException { Status: StatusCodes.Status503ServiceUnavailable } unavailable => unavailable,
            _ when IsOutcomeUnknown(failure) => OutcomeUnknown(failure),
            _ => ApiException.Unavailable(failure),
        };
    }

    public static ApiException OutcomeUnknown(Exception? cause) =>
        new(StatusCodes.Status503ServiceUnavailable, "temporarily_unavailable",
            "Storage did not confirm whether the change was saved; read the current state before repeating the request.", cause);

    private static WriteBatch? Plan(IReadOnlyList<Change> changes, WriteAudit? currentAudit)
    {
        var all = currentAudit is { Committed: false } ? [.. changes, currentAudit.Success] : changes;
        var batch = new WriteBatch();
        var writes = all.Where(change => !change.Assertion).ToList();
        var written = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var change in writes)
        {
            written[change.Identity] = change.Expected;
        }

        foreach (var assertion in all.Where(change => change.Assertion))
        {
            if (written.TryGetValue(assertion.Identity, out var expected))
            {
                if (expected != assertion.Expected)
                {
                    return null;
                }

                continue;
            }

            assertion.Write(batch);
            written[assertion.Identity] = assertion.Expected;
        }

        foreach (var change in writes)
        {
            change.Write(batch);
        }

        return batch;
    }

    private static TResult Committed<TResult>(WriteAudit? currentAudit, TResult result)
    {
        currentAudit?.Committed = true;
        return result;
    }

    private async Task<AuthBatchWrite> WriteAsync(WriteBatch batch, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return batch.Statements <= WriteBatch.MaxStatements
            ? await ExecuteAsync(batch, connection, null, cancellationToken)
            : await ExecuteInTransactionAsync(batch, connection, cancellationToken);
    }

    private static bool Contended(AuthBatchWrite outcome) => outcome switch
    {
        AuthContended => true,
        AuthCompleted or AuthConflict or AuthStorageDown or AuthOutcomeUnknown => false,
    };

    private static async Task<AuthBatchWrite> ExecuteInTransactionAsync(WriteBatch batch, DbConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var executed = await ExecuteAsync(batch, connection, transaction, cancellationToken);
        return executed switch
        {
            AuthCompleted => await CommitAsync(transaction, cancellationToken),
            AuthConflict or AuthContended or AuthStorageDown or AuthOutcomeUnknown => executed,
        };
    }

    private static async Task<AuthBatchWrite> ExecuteAsync(WriteBatch batch, DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        foreach (var guarded in batch.Commands(connection, transaction))
        {
            await using (guarded.Command)
            {
                var executed = transaction is null
                    ? await SendAsync(() => TerraBaseWrites.ExecuteAsync(guarded, false, cancellationToken))
                    : await TerraBaseWrites.ExecuteAsync(guarded, true, cancellationToken);
                var stopped = executed switch
                {
                    AuthCompleted => (AuthBatchWrite?)null,
                    AuthConflict or AuthContended or AuthStorageDown or AuthOutcomeUnknown => executed,
                };
                if (stopped is { } outcome)
                {
                    return outcome;
                }
            }
        }

        return new AuthCompleted();
    }

    private static Task<AuthBatchWrite> CommitAsync(DbTransaction transaction, CancellationToken cancellationToken) =>
        SendAsync(() => TerraBaseWrites.CommitAsync(transaction, cancellationToken));

    private static async Task<AuthBatchWrite> SendAsync(Func<Task<AuthBatchWrite>> send)
    {
        var writes = Tracked.Value;
        writes?.Dispatch();
        var outcome = await send();
        var settled = outcome switch
        {
            AuthConflict or AuthContended or AuthStorageDown => true,
            AuthCompleted or AuthOutcomeUnknown => false,
        };
        if (settled)
        {
            writes?.Settle();
        }

        return outcome;
    }

    private sealed class WriteAudit(Change success)
    {
        public Change Success { get; } = success;

        public bool Committed { get; set; }
    }
}

public sealed class StorageWrites
{
    private int inDoubt;

    public bool MayHaveCommitted => Volatile.Read(ref inDoubt) > 0;

    internal void Dispatch() => Interlocked.Increment(ref inDoubt);

    internal void Settle() => Interlocked.Decrement(ref inDoubt);
}

public sealed class AuthTransaction(DbConnection connection, DbTransaction transaction)
{
    private readonly List<Change> staged = [];

    public DbConnection Connection => connection;

    public DbTransaction Transaction => transaction;

    internal IReadOnlyList<Change> Staged => staged;

    public void Stage(Change change)
    {
        ArgumentNullException.ThrowIfNull(change);
        staged.Add(change);
    }
}
