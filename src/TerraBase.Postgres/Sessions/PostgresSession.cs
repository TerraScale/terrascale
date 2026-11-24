using System.Security.Cryptography;
using System.Threading.Channels;
using TerraBase.Postgres.Hosting;
using TerraBase.Postgres.Protocol;
using TerraBase.Postgres.Sql;
using TerraScale.Contracts.Consistency;
using TerraScale.Contracts.Continuity;

namespace TerraBase.Postgres.Sessions;

internal sealed class PgPreparedStatement(string name, PgCommand command, int[] parameterOids)
{
    public string Name => name;

    public PgCommand Command => command;

    public int[] ParameterOids => parameterOids;

    public IReadOnlyList<PgColumn>? Columns { get; set; }

    public bool Described { get; set; }
}

internal sealed class PgPortal(PgPreparedStatement statement, IReadOnlyList<SqlValue> parameters, long parameterBytes, short[] resultFormats)
{
    private long _resultBytes;

    public PgPreparedStatement Statement => statement;

    public IReadOnlyList<SqlValue> Parameters => parameters;

    public short[] ResultFormats => resultFormats;

    public PgResult? Result { get; private set; }

    public long ParameterBytes => parameterBytes;

    public long Bytes => parameterBytes + PgValueCodec.Footprint(Columns) + _resultBytes;

    public void Hold(PgResult result, long bytes)
    {
        Result = result;
        _resultBytes = bytes;
    }

    public void Release()
    {
        Result = null;
        _resultBytes = 0;
    }

    public int Position { get; set; }

    public bool Completed { get; set; }

    public IReadOnlyList<PgColumn>? Columns { get; set; }
}

internal sealed class PostgresTransactionBlock(ConsistencyMode mode, SqlAccess access, bool isImplicit, long started)
{
    public long Started => started;

    public bool Savepoints { get; set; }

    public ConsistencyMode Mode { get; set; } = mode;

    public SqlAccess Access { get; set; } = access;

    public bool Implicit => isImplicit;

    public ISqlTransaction? Active { get; set; }

    public bool Failed { get; set; }
}

internal sealed class PostgresSession(PostgresGrant grant, PostgresStartup startup, TimeProvider timeProvider) : IPostgresCancellable
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _statement;
    private bool _canceled;

    public int ProcessId { get; set; }

    public int SecretKey { get; } = RandomNumberGenerator.GetInt32(int.MaxValue);

    public PostgresGrant Grant => grant;

    public TimeProvider Time => timeProvider;

    public IPostgresDatabaseExecutor Executor => grant.Executor;

    public PgSettings Settings { get; } = new(startup.User, startup.ApplicationName);

    public PgRewriteContext RewriteContext => new(grant.DatabaseName, startup.User, ProcessId, Settings.Values);

    public CausalBookmark Bookmark { get; private set; } = CausalBookmark.Empty;

    public PostgresTransactionBlock? Transaction { get; set; }

    public Dictionary<string, PgPreparedStatement> Statements { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, PgPortal> Portals { get; } = new(StringComparer.Ordinal);

    public long PortalBytes(string? except = null)
    {
        long total = 0;
        foreach (var (name, portal) in Portals)
        {
            if (name != except)
            {
                total += portal.Bytes;
            }
        }

        return total;
    }

    public bool TryHold(string name, PgPortal portal, PgResult result)
    {
        var bytes = PgValueCodec.Footprint(result.Rows);
        var columns = PgValueCodec.Footprint(portal.Columns ?? result.Columns);
        if (PortalBytes(except: name) + portal.ParameterBytes + columns + bytes > PostgresLimits.MaxPortalBytes)
        {
            return false;
        }

        portal.Hold(result, bytes);
        return true;
    }

    public Dictionary<string, string> PendingReports { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Channels { get; } = new(StringComparer.Ordinal);

    public Channel<ChangefeedEntry> Notifications { get; } = Channel.CreateBounded<ChangefeedEntry>(
        new BoundedChannelOptions(PostgresLimits.NotificationQueue) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });

    public CancellationTokenSource? Subscription { get; set; }

    public Task? SubscriptionTask { get; set; }

    public bool SkipUntilSync { get; set; }

    public char Status => Transaction is null ? 'I' : Transaction.Failed ? 'E' : 'T';

    public void Observe(CausalBookmark bookmark)
    {
        var merged = Bookmark.Merge(bookmark);
        if (merged.Equals(Bookmark))
        {
            return;
        }

        Bookmark = merged;
        var encoded = merged.Encode();
        Settings.SetInternal(PgSettings.Bookmark, encoded);
        PendingReports[PgSettings.Bookmark] = encoded;
    }

    public void Report(PgSettingChange change)
    {
        if (change.Reported)
        {
            PendingReports[change.Name] = change.Value;
        }
    }

    public PostgresStatementScope BeginStatement(CancellationToken connection)
    {
        var timeout = new CancellationTokenSource(Settings.StatementTimeout, timeProvider);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(connection, timeout.Token, grant.Revocation, grant.Shutdown);
        lock (_gate)
        {
            _statement = linked;
            _canceled = false;
        }

        return new PostgresStatementScope(this, linked, timeout);
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _canceled = true;
            _statement?.Cancel();
        }
    }

    internal bool EndStatement(CancellationTokenSource linked)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_statement, linked))
            {
                _statement = null;
            }

            return _canceled;
        }
    }
}

internal sealed class PostgresStatementScope(PostgresSession session, CancellationTokenSource linked, CancellationTokenSource timeout) : IDisposable
{
    public CancellationToken Token => linked.Token;

    public bool TimedOut => timeout.IsCancellationRequested;

    public bool Canceled { get; private set; }

    public void Dispose()
    {
        Canceled = session.EndStatement(linked);
        linked.Dispose();
        timeout.Dispose();
    }
}
