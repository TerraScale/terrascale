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
        Columns ??= result.Columns;
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
    private const int MaxParseCacheEntries = 2048;

    private sealed class CachedParseEntry(IReadOnlyList<PgCommand> commands)
    {
        public IReadOnlyList<PgCommand> Commands => commands;

        public int[]? InferredOids { get; set; }
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, CachedParseEntry> _parseCache = new(StringComparer.Ordinal);
    private CancellationTokenSource? _statementCts;
    private CancellationTokenSource? _activeStatement;
    private PgRewriteContext? _rewriteContext;
    private int _processId;
    private bool _canceled;
    private bool _aborted;

    public int ProcessId
    {
        get => _processId;
        set
        {
            _processId = value;
            _rewriteContext = null;
        }
    }

    public int SecretKey { get; } = RandomNumberGenerator.GetInt32(int.MaxValue);

    public PostgresGrant Grant => grant;

    public TimeProvider Time => timeProvider;

    public IPostgresDatabaseExecutor Executor => grant.Executor;

    public PgSettings Settings { get; } = new(startup.User, startup.ApplicationName);

    public PgRewriteContext RewriteContext => _rewriteContext ??= new(grant.DatabaseName, startup.User, _processId, Settings.Values);

    public CausalBookmark Bookmark { get; private set; } = CausalBookmark.Empty;

    public ulong SchemaEpoch { get; private set; } = grant.Executor.SchemaEpoch ?? 0;

    public PostgresTransactionBlock? Transaction { get; set; }

    public Dictionary<string, PgPreparedStatement> Statements { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, PgPortal> Portals { get; } = new(StringComparer.Ordinal);

    public bool TryGetCachedParse(string sql, out IReadOnlyList<PgCommand> commands)
    {
        if (_parseCache.TryGetValue(sql, out var entry))
        {
            commands = entry.Commands;
            return true;
        }

        commands = [];
        return false;
    }

    public void CacheParse(string sql, IReadOnlyList<PgCommand> commands)
    {
        if (!IsCacheable(commands))
        {
            return;
        }

        if (_parseCache.Count >= MaxParseCacheEntries)
        {
            _parseCache.Clear();
        }

        _parseCache[sql] = new CachedParseEntry(commands);
    }

    public bool TryGetCachedInferredOids(string sql, out int[] oids)
    {
        if (_parseCache.TryGetValue(sql, out var entry) && entry.InferredOids is { } cached)
        {
            oids = cached;
            return true;
        }

        oids = [];
        return false;
    }

    public void CacheInferredOids(string sql, int[] oids)
    {
        if (_parseCache.TryGetValue(sql, out var entry))
        {
            entry.InferredOids = oids;
        }
    }

    public void ObserveCurrentSchemaEpoch()
    {
        if (grant.Executor.SchemaEpoch is { } epoch)
        {
            ObserveSchemaEpoch(epoch);
        }
    }

    public void ObserveSchemaEpoch(ulong epoch)
    {
        if (epoch == SchemaEpoch)
        {
            return;
        }

        SchemaEpoch = epoch;
        _parseCache.Clear();
    }

    public void InvalidateSchemaCache() => _parseCache.Clear();

    public long PortalBytes(string? except = null)
    {
        if (Portals.Count == 0 || (Portals.Count == 1 && except is not null && Portals.ContainsKey(except)))
        {
            return 0;
        }

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
        var bytes = PgValueCodec.Footprint(result.Rows)
            + (portal.Columns is { } kept && !ReferenceEquals(kept, result.Columns) ? PgValueCodec.Footprint(result.Columns) : 0);
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

    internal bool WasCanceledOrAborted
    {
        get
        {
            lock (_gate)
            {
                return _canceled || _aborted;
            }
        }
    }

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
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_statementCts is { } existing && existing.TryReset())
            {
                cts = existing;
            }
            else
            {
                _statementCts?.Dispose();
                cts = new CancellationTokenSource(Timeout.InfiniteTimeSpan, timeProvider);
                _statementCts = cts;
            }

            _activeStatement = cts;
            _canceled = false;
            _aborted = false;
            if (connection.IsCancellationRequested || grant.Revocation.IsCancellationRequested || grant.Shutdown.IsCancellationRequested)
            {
                _aborted = true;
                cts.Cancel();
            }
            else
            {
                cts.CancelAfter(Settings.StatementTimeout);
            }
        }

        return new PostgresStatementScope(this, cts);
    }

    public void AbortStatement()
    {
        lock (_gate)
        {
            _aborted = true;
            _activeStatement?.Cancel();
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _canceled = true;
            _activeStatement?.Cancel();
        }
    }

    internal void EndStatement(CancellationTokenSource cts)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_activeStatement, cts))
            {
                _activeStatement = null;
            }

            if (!cts.IsCancellationRequested)
            {
                cts.CancelAfter(Timeout.InfiniteTimeSpan);
            }
        }
    }

    internal void DisposeStatementCts()
    {
        lock (_gate)
        {
            _activeStatement = null;
            _statementCts?.Dispose();
            _statementCts = null;
        }
    }

    private static bool IsCacheable(IReadOnlyList<PgCommand> commands)
    {
        for (var index = 0; index < commands.Count; index++)
        {
            if (commands[index] is PgSqlCommand { Rewrite.DependsOnSettings: true })
            {
                return false;
            }
        }

        return true;
    }
}

internal readonly struct PostgresStatementScope(PostgresSession session, CancellationTokenSource cts) : IDisposable
{
    public CancellationToken Token => cts.Token;

    public bool TimedOut => cts.IsCancellationRequested && !session.WasCanceledOrAborted;

    public void Dispose() => session.EndStatement(cts);
}
