using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;
using Turso.Data.Sqlite;

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

    public Dictionary<string, string> Rows()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-replica-").FullName;
        try
        {
            var file = Path.Combine(directory, "replica.db");
            using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write))
            {
                for (uint number = 1; number <= Size; number++)
                {
                    stream.Write(_pages[number]);
                }
            }

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ConnectionString);
            connection.Open();
            using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA integrity_check";
            if (check.ExecuteScalar() as string != "ok")
            {
                throw new InvalidDataException("the replica image fails its integrity check");
            }

            using var query = connection.CreateCommand();
            query.CommandText = "SELECT id, v FROM items";
            using var reader = query.ExecuteReader();
            var rows = new Dictionary<string, string>(StringComparer.Ordinal);
            while (reader.Read())
            {
                rows[reader.GetString(0)] = reader.GetString(1);
            }

            return rows;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

internal sealed class RevisionDatabase
{
    private const int PerRequest = 25;
    private readonly Dictionary<string, string> _acked = [with(StringComparer.Ordinal)];
    private ManagedDatabaseRef? _reference;
    private (ManagementApi Api, HttpClient Client)? _client;

    public TenantContext Database { get; private set; } = null!;

    public async Task CreateAsync(ManagementApi api)
    {
        _reference = await api.CreateManagedDatabaseAsync();
        Database = new TenantContext(_reference.Organization, PlanTier.Hobby, _reference.Project, _reference.Id);
        await ExecuteAsync(api, [Statement("CREATE TABLE items(id TEXT PRIMARY KEY, v TEXT NOT NULL)")]);
    }

    public async Task InsertAsync(ManagementApi api, int rows)
    {
        for (var done = 0; done < rows; done += PerRequest)
        {
            var batch = Enumerable.Range(0, Math.Min(PerRequest, rows - done))
                .Select(_ => (Id: EntityIds.New().ToString(), Value: $"v-{EntityIds.New()}-{new string('x', 900)}"))
                .ToList();
            await ExecuteAsync(api, [.. batch.Select(row => Statement("INSERT INTO items(id, v) VALUES (?, ?)", row.Id, row.Value))]);
            foreach (var (id, value) in batch)
            {
                _acked[id] = value;
            }
        }
    }

    public async Task UpdateAsync(ManagementApi api, int rows)
    {
        var tail = new string('y', 900);
        foreach (var chunk in _acked.Keys.Take(rows).Chunk(PerRequest).ToList())
        {
            await ExecuteAsync(api, [Statement($"UPDATE items SET v = 'u-' || id || '-' || ? WHERE id IN ({Marks(chunk.Length)})", [tail, .. chunk])]);
            foreach (var id in chunk)
            {
                _acked[id] = $"u-{id}-{tail}";
            }
        }
    }

    public async Task DeleteAsync(ManagementApi api, int rows)
    {
        foreach (var chunk in _acked.Keys.Skip(rows).Take(rows).Chunk(PerRequest).ToList())
        {
            await ExecuteAsync(api, [Statement($"DELETE FROM items WHERE id IN ({Marks(chunk.Length)})", chunk)]);
            foreach (var id in chunk)
            {
                _acked.Remove(id);
            }
        }
    }

    public async Task<int> LostAckedWritesAsync(ManagementApi api)
    {
        var body = await ExecuteAsync(api, [Statement("SELECT id, v FROM items")]);
        var rows = body.GetProperty("results")[0].GetProperty("rows").EnumerateArray()
            .ToDictionary(row => row[0].GetString()!, row => row[1].GetString()!, StringComparer.Ordinal);
        return _acked.Count(row => !rows.TryGetValue(row.Key, out var value) || value != row.Value) + rows.Keys.Count(id => !_acked.ContainsKey(id));
    }

    public int Differences(RevisionReplica replica)
    {
        var rows = replica.Rows();
        return _acked.Count(row => !rows.TryGetValue(row.Key, out var value) || value != row.Value) + rows.Keys.Count(id => !_acked.ContainsKey(id));
    }

    private async Task<JsonElement> ExecuteAsync(ManagementApi api, object[] statements)
    {
        if (_client is not { } open || !ReferenceEquals(open.Api, api))
        {
            _client?.Client.Dispose();
            var key = await api.Server.IssueNarrowedAsync(_reference!.Organization, _reference.Project, null, "documents:read", "documents:write");
            _client = open = (api, api.DataClient(key));
        }

        using var response = await open.Client.PostAsJsonAsync(ManagementApi.Uri(_reference!.Data + "/sql/execute"), new { statements });
        var body = JsonElement.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(body.GetRawText());
        return body;
    }

    private static object Statement(string sql, params string[] args) => new { sql, args };

    private static string Marks(int count) => string.Join(", ", Enumerable.Repeat("?", count));
}
