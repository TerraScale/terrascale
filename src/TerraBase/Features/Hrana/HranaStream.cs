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

    private readonly Dictionary<int, (string Sql, IMemoryReservation Memory)> _sql = [];
    private long _sqlBytes;

    public ISqlSession Session { get; } = session;

    public string Owner { get; } = owner;

    public TenantContext Database { get; } = database;

    /// <summary>A stored statement, or null.</summary>
    public string? Sql(int id) => _sql.TryGetValue(id, out var entry) ? entry.Sql : null;

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

        if (memory.TryReserve(MemoryLane.Foreground, bytes, Database) is not { } reservation)
        {
            return new HranaError("organization memory budget exceeded", "SQLITE_BUSY");
        }

        _sql.Add(id, (sql, reservation));
        _sqlBytes += bytes;
        return null;
    }

    /// <summary>Forgets a stored statement; an unknown id is not an error.</summary>
    public void Forget(int id)
    {
        if (_sql.Remove(id, out var entry))
        {
            _sqlBytes -= (long)entry.Sql.Length * sizeof(char);
            entry.Memory.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sql.Keys.ToList())
        {
            Forget(id);
        }

        await Session.DisposeAsync().ConfigureAwait(false);
    }
}
