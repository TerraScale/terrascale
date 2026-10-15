using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Sqlite;
using TerraBase.Fast.Documents;
using TerraBase.Features.Documents;
using TerraBase.Features.Replication;
using TerraBase.Features.Shapes;
using TerraBase.Tenancy;

namespace TerraBase.Fast.Shapes;

public sealed partial class ShapeResumeTests
{
    [Test]
    public async Task A_resume_redirects_to_the_boundary_before_the_first_uncovered_commit_and_refuses_what_it_cannot_prove()
    {
        var wall = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await DocumentApi.StartAsync(services: services => services.AddSingleton<TimeProvider>(wall));
        var path = $"{api.Collections}/{DocumentApi.Collection}/shape";
        for (var index = 0; index < 3; index++)
        {
            _ = await api.PutAsync(DocumentApi.NewId(), new { index });
        }

        Dictionary<string, string> state = [];
        var synced = await SyncAsync(api.Client, path, null, state);
        var cursor = ChangeCursor.Decode(Base64Url.EncodeToString(Base64Url.DecodeFromChars(synced.Cursor).AsSpan(17)))!;
        await Assert.That(cursor.Issued.Value).IsEqualTo(FeedCursor.At(wall.GetUtcNow()).Value);

        foreach (var header in new[] { false, true })
        {
            var head = await ResumeAsync(api.Client, path, synced.Cursor, header);
            await Assert.That(head.Status).IsEqualTo(HttpStatusCode.TemporaryRedirect).Because(head.Message ?? "no refusal message");
            await Assert.That(head.CacheControl).IsEqualTo("private, no-store");
            await Assert.That(head.Location).IsEqualTo($"{path}?handle={synced.Handle}&offset={synced.Offset}");
        }

        var belowProof = Forge(synced.Cursor, cursor => cursor with
        {
            Prefixes = cursor.Prefixes.ToDictionary(pair => pair.Key, pair => new FeedPrefix(cursor.Stamps[pair.Key], pair.Value.Hash)),
        });
        var replay = await ResumeAsync(api.Client, path, belowProof);
        await Assert.That(replay.Status).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        await Assert.That(replay.Location).IsEqualTo($"{path}?handle={synced.Handle}&offset=0");

        // A write after the cursor is uncovered: the target is the boundary before it, and the client converges from there.
        var written = DocumentApi.NewId();
        _ = await api.PutAsync(written, new { index = 3 });
        var resumed = await ResumeAsync(api.Client, path, synced.Cursor);
        await Assert.That(resumed.Location).IsEqualTo($"{path}?handle={synced.Handle}&offset={synced.Offset}");
        _ = await FollowAsync(api.Client, path, resumed.Location!, state);
        await Assert.That(state.Keys).Contains(written);
        await AssertSameAsync(state, await FreshAsync(api.Client, path));

        // Another issuer with the same prefix is proven; a different prefix, an old cursor, a stamp ahead or another scope is not.
        var other = Forge(synced.Cursor, cursor => cursor with { Issuer = new ReplicaId(cursor.Issuer.Value ^ 1) });
        await Assert.That((await ResumeAsync(api.Client, path, other)).Status).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        foreach (var refused in new[]
        {
            Forge(synced.Cursor, cursor => cursor with { Issuer = new ReplicaId(cursor.Issuer.Value ^ 1), Prefixes = Flipped(cursor) }),
            Forge(synced.Cursor, cursor => cursor with { Issued = FeedCursor.At(DateTimeOffset.UtcNow.AddDays(-8)) }),
            Forge(synced.Cursor, cursor => cursor with { Stamps = cursor.Stamps.ToDictionary(pair => pair.Key, pair => new Hlc(pair.Value.Value + (1UL << 32))) }),
        })
        {
            await AssertRefetchAsync(await ResumeAsync(api.Client, path, refused), synced.Handle);
        }

        var filtered = await ResumeAsync(api.Client, path + "?where=" + Uri.EscapeDataString("""{"exists":"/index"}"""), synced.Cursor);
        await Assert.That(filtered.Status).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(filtered.Code).IsEqualTo("must_refetch");
    }

    [Test]
    public async Task A_resume_in_a_cell_is_capped_at_the_remote_durable_frontier_not_the_local_head()
    {
        var durability = new SwitchedDurability();
        await using var api = await DocumentApi.StartAsync(services: services => services.AddSingleton<IRemoteDurability>(durability));
        var path = $"{api.Collections}/{DocumentApi.Collection}/shape";
        _ = await api.PutAsync(DocumentApi.NewId(), new { index = 0 });
        var synced = await SyncAsync(api.Client, path, null, []);
        await Assert.That(long.Parse(synced.Offset, CultureInfo.InvariantCulture)).IsGreaterThan(0);

        // Rule 4: once the machine confirms, nothing it holds is remote durable (no peer ever proved a commit), so the
        // frontier stays 0 and the target is 0, not the local head the cursor covers.
        durability.Confirms = true;
        var capped = await ResumeAsync(api.Client, path, synced.Cursor);
        await Assert.That(capped.Status).IsEqualTo(HttpStatusCode.TemporaryRedirect).Because(capped.Message ?? "no refusal message");
        await Assert.That(capped.CacheControl).IsEqualTo("private, no-store");
        await Assert.That(capped.Location).IsEqualTo($"{path}?handle={synced.Handle}&offset=0");
    }

    [Test]
    public async Task A_cross_machine_resume_proves_a_chunk_cursor_issued_above_a_feed_retention_baseline()
    {
        await using var api = await DocumentApi.StartAsync();
        var path = $"{api.Collections}/{DocumentApi.Collection}/shape";
        _ = await api.PutAsync(DocumentApi.NewId(), new { index = 0 });
        _ = await api.PutAsync(DocumentApi.NewId(), new { index = 1 });
        var token = TestContext.Current!.Execution.CancellationToken;
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), token))!;
        // Retention deletes only the oldest event: its chain hash becomes the origin's persisted baseline.
        await using (var transaction = await store.BeginTransactionAsync(write: true, token))
        {
            await Assert.That(await transaction.TrimFeedAsync(new Hlc(ulong.MaxValue >> 1), 1, token)).IsEqualTo(1);
            _ = await transaction.CommitAsync(token);
        }

        Dictionary<string, string> state = [];
        var synced = await SyncAsync(api.Client, path, null, state);
        _ = await api.PutAsync(DocumentApi.NewId(), new { index = 2 });
        var chunk = await FollowAsync(api.Client, path, $"{path}?handle={synced.Handle}&offset={synced.Offset}", state);
        var other = Forge(chunk.Cursor, cursor => cursor with { Issuer = new ReplicaId(cursor.Issuer.Value ^ 1) });
        var cursor = ChangeCursor.Decode(Base64Url.EncodeToString(Base64Url.DecodeFromChars(other).AsSpan(17)))!;
        await Assert.That(cursor.Prefixes.Values.Any(prefix => prefix.Floor.Value > 0)).IsTrue();

        var resumed = await ResumeAsync(api.Client, path, other);
        await Assert.That(resumed.Status).IsEqualTo(HttpStatusCode.TemporaryRedirect).Because(resumed.Message ?? "no refusal message");
        await Assert.That(resumed.Location).IsEqualTo($"{path}?handle={chunk.Handle}&offset={chunk.Offset}");
    }

    [Test]
    public async Task A_chunk_cursor_stops_an_origin_below_its_late_uncovered_row_so_a_resume_delivers_it()
    {
        await using var api = await DocumentApi.StartAsync();
        var path = $"{api.Collections}/{DocumentApi.Collection}/shape";
        var token = TestContext.Current!.Execution.CancellationToken;
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), token))!;
        var origin = ReplicaId.Of(4518, 23, 1);
        var now = DateTimeOffset.UtcNow;
        Hlc Stamp(TimeSpan ago)
        {
            return new((ulong)(now - ago).ToUnixTimeMilliseconds() << 16);
        }
        async Task JoinAsync(string id, Hlc stamp, int pad)
        {
            using var body = JsonDocument.Parse(JsonSerializer.Serialize(new { pad = new string('x', pad) }));
            var at = new EntryVersion(stamp, origin);
            await using var write = await store.BeginTransactionAsync(write: true, token);
            _ = await write.JoinAsync(DocumentApi.Collection, id, DocumentState.Bottom.Put(at, body.RootElement), at, token);
            _ = await write.CommitAsync(token);
        }

        var (first, covered, late) = (DocumentApi.NewId(), DocumentApi.NewId(), DocumentApi.NewId());
        await JoinAsync(first, Stamp(TimeSpan.FromMinutes(3)), 1);
        Dictionary<string, string> state = [];
        var synced = await SyncAsync(api.Client, path, null, state);
        // The same origin's late row joins below the covered row's stamp but after it; together they pass the 1 MiB body
        // bound, so the next chunk is sealed with the late row present and ends before it.
        await JoinAsync(covered, Stamp(TimeSpan.FromMinutes(1)), 600_000);
        await JoinAsync(late, Stamp(TimeSpan.FromMinutes(2)), 600_000);
        string issued;
        using (var response = await api.Client.GetAsync(new Uri($"{path}?handle={synced.Handle}&offset={synced.Offset}", UriKind.Relative), token))
        {
            var chunk = JsonElement.Parse(await response.Content.ReadAsStringAsync(token));
            await Assert.That(chunk.GetProperty("up_to_date").GetBoolean()).IsFalse();
            var ids = chunk.GetProperty("changes").EnumerateArray().SelectMany(change => change.GetProperty("ops").EnumerateArray()).Select(op => op.GetProperty("id").GetString());
            await Assert.That(ids.SequenceEqual([covered])).IsTrue();
            issued = chunk.GetProperty("cursor_resume").GetString()!;
        }

        // The minted stamp stops below the late row: naming the covered row's stamp would claim the late row too.
        var cursor = ChangeCursor.Decode(Base64Url.EncodeToString(Base64Url.DecodeFromChars(issued).AsSpan(17)))!;
        await Assert.That(cursor.Stamps[origin]).IsEqualTo(Stamp(TimeSpan.FromMinutes(3)));
        var resumed = await ResumeAsync(api.Client, path, issued);
        await Assert.That(resumed.Status).IsEqualTo(HttpStatusCode.TemporaryRedirect).Because(resumed.Message ?? "no refusal message");
        await Assert.That(resumed.Location).IsEqualTo($"{path}?handle={synced.Handle}&offset={synced.Offset}");
        List<string> applied = [];
        _ = await FollowAsync(api.Client, path, resumed.Location!, state, applied);
        await Assert.That(applied.SequenceEqual([covered, late])).IsTrue().Because(string.Join(',', applied));
        await AssertSameAsync(state, await FreshAsync(api.Client, path));
    }

    [Test]
    public async Task A_resume_target_below_the_retention_floor_is_409()
    {
        await using var api = await DocumentApi.StartAsync();
        var path = $"{api.Collections}/{DocumentApi.Collection}/shape";
        _ = await api.PutAsync(DocumentApi.NewId(), new { index = 0 });
        var synced = await SyncAsync(api.Client, path, null, []);
        _ = await api.PutAsync(DocumentApi.NewId(), new { index = 1 });
        _ = await FollowAsync(api.Client, path, $"{path}?handle={synced.Handle}&offset={synced.Offset}", []);
        var token = TestContext.Current!.Execution.CancellationToken;
        var store = (await api.Server.Services.GetRequiredService<SqliteStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), token))!;
        var retention = new ShapeRetention(new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(8)), api.Server.Services.GetRequiredService<IShapeStorage>());
        await Assert.That(await retention.RunPassAsync(store, synced.Handle, token)).IsGreaterThan(0);
        await Assert.That(await retention.GetFloorAsync(store, synced.Handle, token)).IsGreaterThan(long.Parse(synced.Offset, CultureInfo.InvariantCulture));

        await AssertRefetchAsync(await ResumeAsync(api.Client, path, synced.Cursor), synced.Handle);
    }

    // A cell of one; the test turns on Confirms after syncing.
    private sealed class SwitchedDurability : IRemoteDurability
    {
        private readonly LocalDurability _local = new();

        public bool Confirms { get; set; }

        public ValueTask<IReadOnlyList<string>> OtherRegionsAsync(CancellationToken cancellationToken) => _local.OtherRegionsAsync(cancellationToken);

        public ValueTask<RemoteAdmission> AdmitAsync(TenantContext database, CancellationToken cancellationToken) => _local.AdmitAsync(database, cancellationToken);

        public ValueTask<bool> WaitAsync(TenantContext database, CommitMarker marker, CancellationToken cancellationToken) =>
            _local.WaitAsync(database, marker, cancellationToken);
    }

    private sealed record Synced(string Handle, string Offset, string Cursor);

    private sealed record Reply(HttpStatusCode Status, string? Location, string? CacheControl, string? Handle, string? Code, string? Message);

    private static async Task<Synced> SyncAsync(HttpClient http, string path, string? cursor, Dictionary<string, string> state)
    {
        var bootstrap = await ResumeAsync(http, path, cursor);
        await Assert.That(bootstrap.Status).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        return await FollowAsync(http, path, bootstrap.Location!, state);
    }

    // Reads from a redirect target until up to date, applying each whole response (section 7 rules 1 and 2); `applied` gets each op's id in order.
    private static async Task<Synced> FollowAsync(HttpClient http, string path, string target, Dictionary<string, string> state, List<string>? applied = null)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        while (true)
        {
            using var response = await http.GetAsync(new Uri(target, UriKind.Relative), token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var body = JsonElement.Parse(await response.Content.ReadAsStringAsync(token));
            foreach (var op in body.GetProperty("changes").EnumerateArray().SelectMany(change => change.GetProperty("ops").EnumerateArray()))
            {
                var id = op.GetProperty("id").GetString()!;
                applied?.Add(id);
                if (op.GetProperty("op").GetString() == "upsert")
                {
                    state[id] = op.GetProperty("document").GetRawText();
                }
                else
                {
                    _ = state.Remove(id);
                }
            }

            var synced = new Synced(body.GetProperty("handle").GetString()!, body.GetProperty("next_offset").GetString()!, body.GetProperty("cursor_resume").GetString()!);
            if (body.GetProperty("up_to_date").GetBoolean())
            {
                return synced;
            }

            target = $"{path}?handle={synced.Handle}&offset={synced.Offset}";
        }
    }

    // The bootstrap at -1, with the resume cursor in the query or in TB-Shape-Resume when one is given.
    private static async Task<Reply> ResumeAsync(HttpClient http, string path, string? cursor, bool header = false)
    {
        var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var target = cursor is null || header ? $"{path}{separator}offset=-1" : $"{path}{separator}offset=-1&cursor={cursor}";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(target, UriKind.Relative));
        if (cursor is not null && header)
        {
            request.Headers.Add("TB-Shape-Resume", cursor);
        }

        var token = TestContext.Current!.Execution.CancellationToken;
        using var response = await http.SendAsync(request, token);
        var text = await response.Content.ReadAsStringAsync(token);
        return new Reply(response.StatusCode, response.Headers.Location?.OriginalString, response.Headers.TryGetValues("Cache-Control", out var cache) ? cache.Single() : null,
            response.Headers.TryGetValues("TB-Shape-Handle", out var handle) ? handle.Single() : null,
            text.StartsWith('{') ? JsonElement.Parse(text).GetProperty("error").GetProperty("code").GetString() : null,
            text.StartsWith('{') ? JsonElement.Parse(text).GetProperty("error").GetProperty("message").GetString() : null);
    }

    private static async Task<Dictionary<string, string>> FreshAsync(HttpClient http, string path)
    {
        Dictionary<string, string> state = [];
        _ = await SyncAsync(http, path, null, state);
        return state;
    }

    private static async Task AssertSameAsync(Dictionary<string, string> state, Dictionary<string, string> truth)
    {
        await Assert.That(state.Count).IsEqualTo(truth.Count);
        foreach (var (id, document) in truth)
        {
            await Assert.That(state.GetValueOrDefault(id)).IsEqualTo(document).Because($"document {id} converged");
        }
    }

    private static async Task AssertRefetchAsync(Reply reply, string handle)
    {
        await Assert.That(reply.Status).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(reply.Code).IsEqualTo("must_refetch");
        await Assert.That(reply.Handle).IsEqualTo(handle);
        await Assert.That(reply.CacheControl).IsEqualTo("private, no-store");
    }

    // Re-encodes the envelope's R8 cursor after `change`, keeping its marker and scope bytes.
    private static string Forge(string token, Func<FeedCursor, FeedCursor> change)
    {
        var bytes = Base64Url.DecodeFromChars(token);
        var cursor = ChangeCursor.Decode(Base64Url.EncodeToString(bytes.AsSpan(17)))!;
        return Base64Url.EncodeToString([.. bytes[..17], .. Base64Url.DecodeFromChars(ChangeCursor.Encode(change(cursor)))]);
    }

    private static Dictionary<ReplicaId, FeedPrefix> Flipped(FeedCursor cursor) => cursor.Prefixes.ToDictionary(pair => pair.Key, pair =>
    {
        var hash = pair.Value.Hash.ToArray();
        hash[0] ^= 1;
        return new FeedPrefix(pair.Value.Floor, hash);
    });

}
