using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
using TerraBase.Engine.Turso;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Revisions;

internal sealed class RevisionReplica
{
    private readonly Dictionary<uint, byte[]> _pages = [];

    public string? Revision { get; private set; }

    public uint Size { get; private set; }

    public async Task<RevisionPullResult> PullAsync(TerraBaseTestServer server, TenantContext database)
    {
        var received = new Dictionary<uint, byte[]>();
        var result = await server.Services.GetRequiredService<IDatabaseRevisions>()
            .PullAsync(database, Revision, (number, page) => received[number] = page.ToArray(), CancellationToken.None)
            ?? throw new InvalidOperationException("the database does not exist");
        if (result.Full is not null)
        {
            _pages.Clear();
        }

        foreach (var (number, page) in received)
        {
            _pages[number] = page;
        }

        foreach (var number in _pages.Keys.Where(number => number > result.DatabaseSize).ToList())
        {
            _pages.Remove(number);
        }

        Size = result.DatabaseSize;
        Revision = result.Revision.ToString();
        return result;
    }

    public bool SameAs(RevisionReplica other) =>
        Size == other.Size
        && Enumerable.Range(1, (int)Size).All(number =>
            _pages.TryGetValue((uint)number, out var mine) && other._pages.TryGetValue((uint)number, out var theirs) && mine.AsSpan().SequenceEqual(theirs));

    public bool Holds(string marker)
    {
        var bytes = Encoding.UTF8.GetBytes(marker);
        return _pages.Values.Any(page => page.AsSpan().IndexOf(bytes) >= 0);
    }
}

internal sealed class RevisionDatabase
{
    private readonly Dictionary<string, string> _acked = new(StringComparer.Ordinal);

    public TenantContext Database { get; } = new(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());

    public IReadOnlyDictionary<string, string> Acked => _acked;

    public async Task CreateAsync(TerraBaseTestServer server)
    {
        await Assert.That(await server.Services.GetRequiredService<IStorageEngine>().CreateDatabaseAsync(Database, CancellationToken.None)).IsTrue();
        await ExecuteAsync(server, "CREATE TABLE items(id TEXT PRIMARY KEY, v TEXT NOT NULL)");
    }

    public async Task InsertAsync(TerraBaseTestServer server, int rows, int perWrite = 25)
    {
        for (var done = 0; done < rows; done += perWrite)
        {
            var batch = Enumerable.Range(0, Math.Min(perWrite, rows - done))
                .Select(_ => (Id: EntityIds.New().ToString(), Value: $"v-{EntityIds.New()}-{new string('x', 900)}"))
                .ToList();
            var sql = new StringBuilder("INSERT INTO items(id, v) VALUES ");
            sql.AppendJoin(", ", batch.Select(row => $"('{row.Id}', '{row.Value}')"));
            await ExecuteAsync(server, sql.ToString());
            foreach (var (id, value) in batch)
            {
                _acked[id] = value;
            }
        }
    }

    public async Task UpdateAsync(TerraBaseTestServer server, int rows)
    {
        foreach (var id in _acked.Keys.Take(rows).ToList())
        {
            var value = $"u-{EntityIds.New()}-{new string('y', 900)}";
            await ExecuteAsync(server, $"UPDATE items SET v = '{value}' WHERE id = '{id}'");
            _acked[id] = value;
        }
    }

    public async Task DeleteAsync(TerraBaseTestServer server, int rows)
    {
        foreach (var id in _acked.Keys.Skip(rows).Take(rows).ToList())
        {
            await ExecuteAsync(server, $"DELETE FROM items WHERE id = '{id}'");
            _acked.Remove(id);
        }
    }

    public async Task<int> LostAckedWritesAsync(TerraBaseTestServer server)
    {
        var store = await StoreAsync(server) ?? throw new InvalidOperationException("the database does not exist");
        var rows = await store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "SELECT id, v FROM items";
            using var reader = command.ExecuteReader();
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            while (reader.Read())
            {
                found[reader.GetString(0)] = reader.GetString(1);
            }

            return found;
        }, "a revision test read", CancellationToken.None);
        return _acked.Count(row => !rows.TryGetValue(row.Key, out var value) || value != row.Value) + rows.Keys.Count(id => !_acked.ContainsKey(id));
    }

    public int Missing(RevisionReplica replica) => _acked.Values.Count(value => !replica.Holds(value[..28]));

    public async Task ExecuteAsync(TerraBaseTestServer server, string sql)
    {
        var store = await StoreAsync(server) ?? throw new InvalidOperationException("the database does not exist");
        await store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteNonQuery();
        }, "a revision test write", CancellationToken.None);
    }

    private async Task<TursoDatabaseStore?> StoreAsync(TerraBaseTestServer server) =>
        (TursoDatabaseStore?)await server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(Database, CancellationToken.None);
}
