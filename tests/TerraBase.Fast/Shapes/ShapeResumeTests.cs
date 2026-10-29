using System.Buffers.Text;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Documents;

using TerraBase.Testing;

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
            await CacheControlAssert.EqualAsync(head.CacheControl);
            await Assert.That(head.Location).IsEqualTo($"{path}?handle={synced.Handle}&offset={synced.Offset}");
        }

        var belowProof = Forge(synced.Cursor, cursor => cursor with
        {
            Prefixes = cursor.Prefixes.ToDictionary(pair => pair.Key, pair => new FeedPrefix(cursor.Stamps[pair.Key], pair.Value.Hash)),
        });
        var replay = await ResumeAsync(api.Client, path, belowProof);
        await Assert.That(replay.Status).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        await Assert.That(replay.Location).IsEqualTo($"{path}?handle={synced.Handle}&offset=0");
        var written = DocumentApi.NewId();
        _ = await api.PutAsync(written, new { index = 3 });
        var resumed = await ResumeAsync(api.Client, path, synced.Cursor);
        await Assert.That(resumed.Location).IsEqualTo($"{path}?handle={synced.Handle}&offset={synced.Offset}");
        _ = await FollowAsync(api.Client, path, resumed.Location!, state);
        await Assert.That(state.Keys).Contains(written);
        await AssertSameAsync(state, await FreshAsync(api.Client, path));
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
    public async Task A_cross_machine_resume_proves_a_chunk_cursor_issued_above_a_feed_retention_baseline()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var path = $"{api.Collections}/{DocumentApi.Collection}/shape";
        _ = await api.PutAsync(DocumentApi.NewId(), new { index = 0 });
        _ = await api.PutAsync(DocumentApi.NewId(), new { index = 1 });
        var token = TestContext.Current!.Execution.CancellationToken;
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), token))!;
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

    private sealed record Synced(string Handle, string Offset, string Cursor);

    private sealed record Reply(HttpStatusCode Status, string? Location, string? CacheControl, string? Handle, string? Code, string? Message);

    private static async Task<Synced> SyncAsync(HttpClient http, string path, string? cursor, Dictionary<string, string> state)
    {
        var bootstrap = await ResumeAsync(http, path, cursor);
        await Assert.That(bootstrap.Status).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        return await FollowAsync(http, path, bootstrap.Location!, state);
    }

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
        return new Reply(response.StatusCode, response.Headers.Location?.OriginalString, response.Headers.CacheControl?.ToString(),
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
        await CacheControlAssert.EqualAsync(reply.CacheControl);
    }

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
