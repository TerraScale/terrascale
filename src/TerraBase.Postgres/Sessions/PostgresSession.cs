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

internal sealed class PgPortal(PgPreparedStatement statement, IReadOnlyList<SqlValue> parameters, short[] resultFormats)
{
    public PgPreparedStatement Statement => statement;

    public IReadOnlyList<SqlValue> Parameters => parameters;

    public short[] ResultFormats => resultFormats;

    public PgResult? Result { get; set; }

    public int Position { get; set; }

    public bool Completed { get; set; }

    public IReadOnlyList<PgColumn>? Columns { get; set; }
}

internal sealed class PostgresTransactionBlock(ConsistencyMode mode, SqlAccess access, bool isImplicit)
{
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

    public IPostgresDatabaseExecutor Executor => grant.Executor;

    public PgSettings Settings { get; } = new(startup.User, startup.ApplicationName);

    public PgRewriteContext RewriteContext => new(grant.DatabaseName, startup.User, ProcessId, Settings.Values);

    public CausalBookmark Bookmark { get; private set; } = CausalBookmark.Empty;

    public PostgresTransactionBlock? Transaction { get; set; }

    public Dictionary<string, PgPreparedStatement> Statements { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, PgPortal> Portals { get; } = new(StringComparer.Ordinal);

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
        var linked = CancellationTokenSource.CreateLinkedTokenSource(connection, timeout.Token, grant.Revocation);
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
