using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
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

    public int Missing(RevisionReplica replica) => _acked.Values.Count(value => !replica.Holds(value[..28]));

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
