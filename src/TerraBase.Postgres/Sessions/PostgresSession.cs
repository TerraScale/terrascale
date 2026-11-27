using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Unicode;
using System.Threading.Channels;
using TerraBase.Postgres.Hosting;
using TerraBase.Postgres.Protocol;
using TerraBase.Postgres.Sql;
using TerraScale.Contracts.Consistency;
using TerraScale.Contracts.Continuity;

namespace TerraBase.Postgres.Sessions;

internal sealed class CachedParseEntry(IReadOnlyList<PgCommand> commands, ulong schemaEpoch, long cacheGeneration)
{
    public IReadOnlyList<PgCommand> Commands => commands;

    public ulong SchemaEpoch => schemaEpoch;

    public long CacheGeneration => cacheGeneration;

    public int[]? InferredOids { get; set; }

    public PgColumn[]? InferredColumns { get; set; }
}

internal sealed class PgPreparedStatement(string name, PgCommand command, int[] parameterOids, CachedParseEntry? cacheEntry = null)
{
    private PgCommand _command = command;
    private int[] _parameterOids = parameterOids;

    public string Name => name;

    public PgCommand Command => _command;

    public int[] ParameterOids => _parameterOids;

    public CachedParseEntry? CacheEntry { get; private set; } = cacheEntry;

    public IReadOnlyList<PgColumn>? Columns { get; set; }

    public bool Described { get; set; }

    public void Reset(PgCommand nextCommand, int[] nextParameterOids, CachedParseEntry? nextCacheEntry)
    {
        _command = nextCommand;
        _parameterOids = nextParameterOids;
        CacheEntry = nextCacheEntry;
        Columns = null;
        Described = false;
    }
}

internal sealed class PgPortal(PgPreparedStatement statement, IReadOnlyList<SqlValue> parameters, long parameterBytes, short[] resultFormats)
{
    private PgPreparedStatement _statement = statement;
    private IReadOnlyList<SqlValue> _parameters = parameters;
    private long _parameterBytes = parameterBytes;
    private short[] _resultFormats = resultFormats;
    private long _resultBytes;

    public PgPreparedStatement Statement => _statement;

    public IReadOnlyList<SqlValue> Parameters => _parameters;

    public short[] ResultFormats => _resultFormats;

    public PgResult? Result { get; private set; }

    public long ParameterBytes => _parameterBytes;

    public long Bytes => _parameterBytes + PgValueCodec.Footprint(Columns) + _resultBytes;

    public void Reset(
        PgPreparedStatement nextStatement,
        IReadOnlyList<SqlValue> nextParameters,
        long nextParameterBytes,
        short[] nextResultFormats,
        IReadOnlyList<PgColumn>? nextColumns)
    {
        _statement = nextStatement;
        _parameters = nextParameters;
        _parameterBytes = nextParameterBytes;
        _resultFormats = nextResultFormats;
        Result = null;
        _resultBytes = 0;
        Position = 0;
        Completed = false;
        Columns = nextColumns;
    }

    public void ClearForReuse()
    {
        _parameters = [];
        _parameterBytes = 0;
        _resultFormats = [];
        Result = null;
        _resultBytes = 0;
        Position = 0;
        Completed = false;
        Columns = null;
    }

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
    private const int MaxSharedParseCacheEntries = 4096;
    private const int Utf8SqlCacheSize = 64;
    private const int MaxUtf8SqlCacheBytes = 4096;
    private const int MaxPooledParameters = 8;

    private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromDays(30);
    private static readonly SqlStatement EmptyStatement = new(string.Empty, [], FrozenDictionary<string, SqlValue>.Empty);
    private static readonly ConcurrentDictionary<string, IReadOnlyList<PgCommand>> SharedParseCache = new(StringComparer.Ordinal);
    private static int _sharedParseCacheCount;

    [InlineArray(MaxPooledParameters)]
    private struct ParameterSlotBuffer
    {
        private SqlValue[]? _element;
    }

    private struct Utf8SqlCacheSlot
    {
        public int Hash;
        public byte[]? Utf8;
        public string? Sql;
        public CachedParseEntry? Entry;
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, CachedParseEntry> _parseCache = new(StringComparer.Ordinal);
    private readonly Utf8SqlCacheSlot[] _utf8SqlCache = new Utf8SqlCacheSlot[Utf8SqlCacheSize];
    private readonly SqlStatement[] _singleStatement = [EmptyStatement];
    private ParameterSlotBuffer _unnamedParameterSlots;
    private PgPreparedStatement? _unnamedStatement;
    private PgPortal? _unnamedPortal;
    private bool _hasUnnamedStatement;
    private bool _unnamedStatementShared;
    private bool _hasUnnamedPortal;
    private byte[]? _pendingSqlUtf8;
    private int _pendingSqlUtf8Length;
    private int _pendingSqlHash;
    private string? _pendingSql;
    private int _utf8EvictToggle;
    private long _cacheGeneration;
    private CancellationTokenSource? _statementCts;
    private CancellationTokenSource? _activeStatement;
    private ITimer? _statementTimer;
    private long _statementGeneration;
    private long _statementDeadline;
    private long _timerScheduledDeadline = long.MaxValue;
    private bool _statementDisposed;
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

    public long CacheGeneration => _cacheGeneration;

    public PostgresTransactionBlock? Transaction { get; set; }

    public Dictionary<string, PgPreparedStatement> Statements { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, PgPortal> Portals { get; } = new(StringComparer.Ordinal);

    public int StatementCount => Statements.Count + (_hasUnnamedStatement ? 1 : 0);

    public int PortalCount => Portals.Count + (_hasUnnamedPortal ? 1 : 0);

    public SqlStatement[] SingleStatement(SqlStatement statement)
    {
        _singleStatement[0] = statement;
        return _singleStatement;
    }

    public void ClearSingleStatement() => _singleStatement[0] = EmptyStatement;

    public bool TryGetStatement(string name, [MaybeNullWhen(false)] out PgPreparedStatement statement)
    {
        if (name.Length == 0)
        {
            if (_hasUnnamedStatement && _unnamedStatement is { } unnamed)
            {
                statement = unnamed;
                return true;
            }

            statement = null;
            return false;
        }

        return Statements.TryGetValue(name, out statement);
    }

    private bool IsUnnamedStatementReferenced() =>
        _unnamedStatementShared || (_hasUnnamedPortal && ReferenceEquals(_unnamedPortal?.Statement, _unnamedStatement));

    public void SetStatement(string name, PgCommand command, int[] parameterOids, CachedParseEntry? cacheEntry)
    {
        if (name.Length == 0)
        {
            if (_unnamedStatement is { } existing && !IsUnnamedStatementReferenced())
            {
                existing.Reset(command, parameterOids, cacheEntry);
            }
            else
            {
                _unnamedStatement = new PgPreparedStatement(string.Empty, command, parameterOids, cacheEntry);
                _unnamedStatementShared = false;
            }

            _hasUnnamedStatement = true;
            return;
        }

        Statements[name] = new PgPreparedStatement(name, command, parameterOids, cacheEntry);
    }

    public bool RemoveStatement(string name)
    {
        if (name.Length == 0)
        {
            if (!_hasUnnamedStatement)
            {
                return false;
            }

            _hasUnnamedStatement = false;
            if (IsUnnamedStatementReferenced())
            {
                _unnamedStatement = null;
                _unnamedStatementShared = false;
            }

            return true;
        }

        return Statements.Remove(name);
    }

    public void ClearStatements()
    {
        Statements.Clear();
        _hasUnnamedStatement = false;
        _unnamedStatementShared = false;
        _unnamedStatement = null;
    }

    public bool TryGetPortal(string name, [MaybeNullWhen(false)] out PgPortal portal)
    {
        if (name.Length == 0)
        {
            if (_hasUnnamedPortal && _unnamedPortal is { } unnamed)
            {
                portal = unnamed;
                return true;
            }

            portal = null;
            return false;
        }

        return Portals.TryGetValue(name, out portal);
    }

    public SqlValue[] RentUnnamedParameters(int count)
    {
        if (count is > 0 and <= MaxPooledParameters)
        {
            return _unnamedParameterSlots[count - 1] ??= new SqlValue[count];
        }

        return new SqlValue[count];
    }

    public void SetPortal(
        string name,
        PgPreparedStatement statement,
        IReadOnlyList<SqlValue> parameters,
        long parameterBytes,
        short[] resultFormats,
        IReadOnlyList<PgColumn>? columns)
    {
        if (name.Length == 0)
        {
            if (_hasUnnamedPortal
                && _unnamedPortal is { Parameters: SqlValue[] previous }
                && !ReferenceEquals(previous, parameters)
                && previous.Length is > 0 and <= MaxPooledParameters
                && ReferenceEquals(previous, _unnamedParameterSlots[previous.Length - 1]))
            {
                Array.Clear(previous);
            }

            if (_unnamedPortal is { } existing)
            {
                existing.Reset(statement, parameters, parameterBytes, resultFormats, columns);
            }
            else
            {
                _unnamedPortal = new PgPortal(statement, parameters, parameterBytes, resultFormats) { Columns = columns };
            }

            _hasUnnamedPortal = true;
            return;
        }

        if (ReferenceEquals(statement, _unnamedStatement))
        {
            _unnamedStatementShared = true;
        }

        Portals[name] = new PgPortal(statement, parameters, parameterBytes, resultFormats) { Columns = columns };
    }

    public void RemovePortal(string name)
    {
        if (name.Length == 0)
        {
            if (_hasUnnamedPortal)
            {
                if (_unnamedPortal is { Parameters: SqlValue[] pooled }
                    && pooled.Length is > 0 and <= MaxPooledParameters
                    && ReferenceEquals(pooled, _unnamedParameterSlots[pooled.Length - 1]))
                {
                    Array.Clear(pooled);
                }

                _unnamedPortal?.ClearForReuse();
                _hasUnnamedPortal = false;
            }

            return;
        }

        Portals.Remove(name);
        if (Portals.Count == 0)
        {
            _unnamedStatementShared = false;
        }
    }

    public void ClearPortals()
    {
        Portals.Clear();
        _unnamedStatementShared = false;
        RemovePortal(string.Empty);
    }

    public bool TryResolveUtf8Sql(ReadOnlySpan<byte> utf8, out string sql, out CachedParseEntry? entry)
    {
        if (utf8.IsEmpty)
        {
            sql = string.Empty;
            entry = TryGetCachedParseEntry(string.Empty, out var emptyEntry) ? emptyEntry : null;
            return true;
        }

        var hash = HashUtf8(utf8);
        var baseIndex = (hash & ((Utf8SqlCacheSize / 2) - 1)) << 1;
        ref var slot0 = ref _utf8SqlCache[baseIndex];
        if (slot0.Hash == hash
            && slot0.Utf8 is { } bytes0
            && bytes0.AsSpan().SequenceEqual(utf8)
            && slot0.Sql is { } sql0
            && slot0.Entry is { } entry0
            && entry0.CacheGeneration == _cacheGeneration)
        {
            sql = sql0;
            entry = entry0;
            return true;
        }

        ref var slot1 = ref _utf8SqlCache[baseIndex + 1];
        if (slot1.Hash == hash
            && slot1.Utf8 is { } bytes1
            && bytes1.AsSpan().SequenceEqual(utf8)
            && slot1.Sql is { } sql1
            && slot1.Entry is { } entry1
            && entry1.CacheGeneration == _cacheGeneration)
        {
            sql = sql1;
            entry = entry1;
            return true;
        }

        if (!Utf8.IsValid(utf8))
        {
            sql = string.Empty;
            entry = null;
            return false;
        }

        sql = Encoding.UTF8.GetString(utf8);
        if (TryGetCachedParseEntry(sql, out var existing))
        {
            if (utf8.Length <= MaxUtf8SqlCacheBytes)
            {
                StoreUtf8SqlCache(hash, utf8.ToArray(), sql, existing);
            }

            entry = existing;
            return true;
        }

        if (utf8.Length <= MaxUtf8SqlCacheBytes)
        {
            if (_pendingSqlUtf8 is null || _pendingSqlUtf8.Length < utf8.Length)
            {
                _pendingSqlUtf8 = new byte[Math.Max(256, utf8.Length)];
            }

            utf8.CopyTo(_pendingSqlUtf8);
            _pendingSqlUtf8Length = utf8.Length;
            _pendingSqlHash = hash;
            _pendingSql = sql;
        }
        else
        {
            _pendingSql = null;
        }

        entry = null;
        return true;
    }

    public bool TryGetCachedParse(string sql, out IReadOnlyList<PgCommand> commands)
    {
        if (TryGetCachedParseEntry(sql, out var entry))
        {
            commands = entry.Commands;
            return true;
        }

        commands = [];
        return false;
    }

    public bool TryGetCachedParseEntry(string sql, [MaybeNullWhen(false)] out CachedParseEntry entry)
    {
        if (_parseCache.TryGetValue(sql, out entry))
        {
            return true;
        }

        if (SharedParseCache.TryGetValue(sql, out var shared))
        {
            entry = StoreLocalParse(sql, shared);
            return true;
        }

        entry = null;
        return false;
    }

    public CachedParseEntry? CacheParse(string sql, IReadOnlyList<PgCommand> commands)
    {
        if (!IsCacheable(commands))
        {
            return null;
        }

        if (IsShareable(commands) && !SharedParseCache.ContainsKey(sql) && SharedParseCache.TryAdd(sql, commands))
        {
            if (Interlocked.Increment(ref _sharedParseCacheCount) > MaxSharedParseCacheEntries)
            {
                SharedParseCache.Clear();
                Interlocked.Exchange(ref _sharedParseCacheCount, 0);
            }
        }

        return StoreLocalParse(sql, commands);
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
        InvalidateSchemaCache();
    }

    public void InvalidateSchemaCache()
    {
        _cacheGeneration++;
        _parseCache.Clear();
        Array.Clear(_utf8SqlCache);
        _pendingSql = null;
    }

    public long PortalBytes(string? except = null)
    {
        var total = _hasUnnamedPortal && except != string.Empty && _unnamedPortal is { } unnamed ? unnamed.Bytes : 0L;
        if (Portals.Count == 0 || (Portals.Count == 1 && except is { Length: > 0 } && Portals.ContainsKey(except)))
        {
            return total;
        }

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
        long generation;
        lock (_gate)
        {
            if (_statementCts is { } existing && existing.TryReset())
            {
                cts = existing;
            }
            else
            {
                _statementCts?.Dispose();
                cts = new CancellationTokenSource();
                _statementCts = cts;
            }

            _activeStatement = cts;
            generation = ++_statementGeneration;
            _canceled = false;
            _aborted = false;
            if (connection.IsCancellationRequested || grant.Revocation.IsCancellationRequested || grant.Shutdown.IsCancellationRequested)
            {
                _aborted = true;
                cts.Cancel();
            }
            else if (!_statementDisposed)
            {
                var timeout = Settings.StatementTimeout;
                if (timeout <= TimeSpan.Zero)
                {
                    _statementDeadline = timeProvider.GetTimestamp();
                    cts.Cancel();
                }
                else
                {
                    var clamped = timeout > MaxTimerDelay ? MaxTimerDelay : timeout;
                    var now = timeProvider.GetTimestamp();
                    var deadline = AddTimestamp(now, clamped);
                    _statementDeadline = deadline;
                    if (_statementTimer is null)
                    {
                        _timerScheduledDeadline = deadline;
                        _statementTimer = timeProvider.CreateTimer(static state =>
                        {
                            if (state is PostgresSession owner)
                            {
                                owner.OnStatementTimer();
                            }
                        }, this, clamped, Timeout.InfiniteTimeSpan);
                    }
                    else if (deadline < _timerScheduledDeadline)
                    {
                        _timerScheduledDeadline = deadline;
                        _statementTimer.Change(clamped, Timeout.InfiniteTimeSpan);
                    }
                }
            }
        }

        return new PostgresStatementScope(this, cts, generation);
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

    internal void EndStatement(CancellationTokenSource cts, long generation)
    {
        lock (_gate)
        {
            if (_statementGeneration == generation && ReferenceEquals(_activeStatement, cts))
            {
                _activeStatement = null;
                _statementGeneration++;
            }
        }
    }

    internal void DisposeStatementCts()
    {
        lock (_gate)
        {
            _statementDisposed = true;
            _activeStatement = null;
            _statementGeneration++;
            _timerScheduledDeadline = long.MaxValue;
            _statementTimer?.Dispose();
            _statementTimer = null;
            _statementCts?.Dispose();
            _statementCts = null;
        }
    }

    private void OnStatementTimer()
    {
        lock (_gate)
        {
            if (_statementDisposed)
            {
                return;
            }

            _timerScheduledDeadline = long.MaxValue;
            if (_activeStatement is not { } active)
            {
                return;
            }

            var remaining = timeProvider.GetElapsedTime(timeProvider.GetTimestamp(), _statementDeadline);
            if (remaining <= TimeSpan.Zero)
            {
                active.Cancel();
                return;
            }

            var clamped = remaining > MaxTimerDelay ? MaxTimerDelay : remaining;
            _timerScheduledDeadline = _statementDeadline;
            _statementTimer?.Change(clamped, Timeout.InfiniteTimeSpan);
        }
    }

    private long AddTimestamp(long now, TimeSpan duration)
    {
        var ticks = timeProvider.TimestampFrequency == TimeSpan.TicksPerSecond
            ? duration.Ticks
            : (long)(duration.Ticks * ((double)timeProvider.TimestampFrequency / TimeSpan.TicksPerSecond));
        return now > long.MaxValue - ticks ? long.MaxValue : now + ticks;
    }

    private CachedParseEntry StoreLocalParse(string sql, IReadOnlyList<PgCommand> commands)
    {
        if (_parseCache.Count >= MaxParseCacheEntries)
        {
            _parseCache.Clear();
            Array.Clear(_utf8SqlCache);
        }

        var entry = new CachedParseEntry(commands, SchemaEpoch, _cacheGeneration);
        _parseCache[sql] = entry;
        if (ReferenceEquals(_pendingSql, sql) && _pendingSqlUtf8 is { } pending)
        {
            StoreUtf8SqlCache(_pendingSqlHash, pending.AsSpan(0, _pendingSqlUtf8Length).ToArray(), sql, entry);
            _pendingSql = null;
        }

        return entry;
    }

    private void StoreUtf8SqlCache(int hash, byte[] utf8, string sql, CachedParseEntry entry)
    {
        var baseIndex = (hash & ((Utf8SqlCacheSize / 2) - 1)) << 1;
        ref var slot0 = ref _utf8SqlCache[baseIndex];
        if (slot0.Entry is null || slot0.Entry.CacheGeneration != _cacheGeneration || ReferenceEquals(slot0.Sql, sql))
        {
            slot0 = new Utf8SqlCacheSlot { Hash = hash, Utf8 = utf8, Sql = sql, Entry = entry };
            return;
        }

        ref var slot1 = ref _utf8SqlCache[baseIndex + 1];
        if (slot1.Entry is null || slot1.Entry.CacheGeneration != _cacheGeneration || ReferenceEquals(slot1.Sql, sql))
        {
            slot1 = new Utf8SqlCacheSlot { Hash = hash, Utf8 = utf8, Sql = sql, Entry = entry };
            return;
        }

        var victim = baseIndex + (_utf8EvictToggle++ & 1);
        _utf8SqlCache[victim] = new Utf8SqlCacheSlot { Hash = hash, Utf8 = utf8, Sql = sql, Entry = entry };
    }

    private static int HashUtf8(ReadOnlySpan<byte> utf8)
    {
        var hash = new HashCode();
        hash.AddBytes(utf8);
        return hash.ToHashCode();
    }

    private static bool IsCacheable(IReadOnlyList<PgCommand> commands)
    {
        for (var index = 0; index < commands.Count; index++)
        {
            if (commands[index] is PgSqlCommand { Rewrite.DependsOnSettings: true } or PgCopyToCommand { Query.Rewrite.DependsOnSettings: true })
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsShareable(IReadOnlyList<PgCommand> commands)
    {
        for (var index = 0; index < commands.Count; index++)
        {
            if (commands[index] is PgSqlCommand { Rewrite: { DependsOnSettings: true } or { DependsOnSessionIdentity: true } }
                or PgCopyToCommand { Query.Rewrite: { DependsOnSettings: true } or { DependsOnSessionIdentity: true } })
            {
                return false;
            }
        }

        return true;
    }
}

internal readonly struct PostgresStatementScope(PostgresSession session, CancellationTokenSource cts, long generation) : IDisposable
{
    public CancellationToken Token => cts.Token;

    public bool TimedOut => cts.IsCancellationRequested && !session.WasCanceledOrAborted;

    public void Dispose() => session.EndStatement(cts, generation);
}
