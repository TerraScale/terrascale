using TerraBase.Features.Memory;
using TerraBase.Features.Sql;
using TerraBase.Tenancy;

namespace TerraBase.Features.Hrana;

/// <summary>One stream: its SQL session and the statements stored with <c>store_sql</c>.</summary>
internal sealed class HranaStream(ISqlSession session, string owner, TenantContext database)
{
    /// <summary>Stored statements per stream.</summary>
    public const int MaxStoredSql = 256;

    /// <summary>Stored statement bytes (UTF-16) per stream.</summary>
    public const int MaxStoredSqlBytes = 256 * 1024;

    private readonly Dictionary<int, string> _sql = [];
    private long _sqlBytes;

    // One reservation per stream covers every stored statement: the budget caps how many reservations live at once.
    private IMemoryReservation? _memory;

    public ISqlSession Session { get; } = session;

    public string Owner { get; } = owner;

    public TenantContext Database { get; } = database;

    public long ParkedAt { get; set; }

    /// <summary>Whether a fresh session is equivalent: no transaction and no stored statements.</summary>
    public bool IsStateless => Session.IsAutocommit && _sql.Count == 0;

    /// <summary>A stored statement, or null.</summary>
    public string? Sql(int id) => _sql.GetValueOrDefault(id);

    /// <summary>Stores <paramref name="sql"/> under <paramref name="id"/>, or returns why it cannot.</summary>
    public HranaError? Store(int id, string sql, IMemoryBudget memory)
    {
        var bytes = (long)sql.Length * sizeof(char);
        if (_sql.ContainsKey(id))
        {
            return new HranaError("sql_id is already in use", "SQL_ID_IN_USE");
        }

        if (_sql.Count >= MaxStoredSql || _sqlBytes + bytes > MaxStoredSqlBytes)
        {
            return new HranaError($"a stream stores at most {MaxStoredSql} statements and {MaxStoredSqlBytes / 1024} KiB", "SQLITE_TOOBIG");
        }

        // Grows by replacing: the larger reservation is taken before the smaller one is released.
        if (_sqlBytes + bytes > (_memory?.Bytes ?? 0))
        {
            var size = Math.Min(MaxStoredSqlBytes, Math.Max(_sqlBytes + bytes, 2 * (_memory?.Bytes ?? 2048)));
            if (memory.TryReserve(MemoryLane.Foreground, size, Database) is not { } grown)
            {
                return new HranaError("organization memory budget exceeded", "SQLITE_BUSY");
            }

            _memory?.Dispose();
            _memory = grown;
        }

        _sql.Add(id, sql);
        _sqlBytes += bytes;
        return null;
    }

    /// <summary>Forgets a stored statement; an unknown id is not an error. The reservation shrinks only to nothing.</summary>
    public void Forget(int id)
    {
        if (_sql.Remove(id, out var sql))
        {
            _sqlBytes -= (long)sql.Length * sizeof(char);
        }

        if (_sql.Count == 0)
        {
            _memory?.Dispose();
            _memory = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _sql.Clear();
        _memory?.Dispose();
        _memory = null;
        await Session.DisposeAsync().ConfigureAwait(false);
    }
}
