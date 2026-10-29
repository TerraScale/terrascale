using TerraBase.Testing;
using System.Net;
using System.Text.Json;
using TerraBase.Engine;
using static TerraBase.Fast.Documents.CollectionAccessRulesTests;

namespace TerraBase.Fast.Documents;

public sealed class CollectionAccessChangesTests
{
    [Test]
    [Arguments("public")]
    [Arguments("role")]
    [Arguments("owner")]
    public async Task Fresh_feeds_exclude_previous_collection_generations_before_every_read_grant(string grant)
    {
        await using var api = await CollectionAccessFixture.StartAsync();
        var actor = DocumentApi.NewId();
        var deleted = DocumentApi.NewId();
        var reused = DocumentApi.NewId();
        (await api.PutAsync(deleted, new { secret = "deleted" })).Status.ShouldBe(HttpStatusCode.Created);
        (await api.SendAsync(HttpMethod.Delete, api.Document(deleted))).Status.ShouldBe(HttpStatusCode.OK);
        (await api.PutAsync(reused, new { secret = "old" })).Status.ShouldBe(HttpStatusCode.Created);
        (await api.SendAsync(HttpMethod.Delete, $"{api.Collections}/items")).Status.ShouldBe(HttpStatusCode.OK);
        (await api.SendAsync(HttpMethod.Post, api.Collections, new { name = "items" })).Status.ShouldBe(HttpStatusCode.Created);
        var rules = grant == "owner" ? OwnerRules() : new CollectionAccessRules(null,
            new CollectionAccessPolicy(false, grant == "public", grant == "role" ? ["reader"] : []),
            new CollectionAccessPolicy(false, false, []));
        await DeclareAsync(api, rules);
        (await api.PutAsync(reused, new { owner = actor, value = "current" })).Status.ShouldBe(HttpStatusCode.Created);
        var current = DocumentApi.NewId();
        (await api.PutAsync(current, new { owner = actor, value = "current" })).Status.ShouldBe(HttpStatusCode.Created);
        using var user = Reader(api, actor, "reader");
        using var stream = await Stream.OpenAsync(user, api.DatabasePath() + "/changes?collections=items");
        await Assert.That((await stream.NextAsync()).Name).IsEqualTo("cursor");
        var changes = new List<Event>();
        Event item;
        while ((item = await stream.NextAsync()).Name == "change")
        {
            changes.Add(item);
        }
        await Assert.That(item.Name).IsEqualTo("cursor");
        await Assert.That(changes.Select(change => change.Data.GetProperty("id").GetString()!).ToArray()).IsEquivalentTo([reused, current]);
        foreach (var change in changes)
        {
            await Assert.That(change.Data.GetProperty("collection").GetString()).IsEqualTo("items");
            await Assert.That(change.Data.GetProperty("op").GetString()).IsEqualTo("upsert");
            await Assert.That(change.Data.GetProperty("document").GetProperty("value").GetString()).IsEqualTo("current");
            await Assert.That(change.Data.TryGetProperty("collection_generation", out _)).IsFalse();
            await Assert.That(change.Data.TryGetProperty("access_before", out _)).IsFalse();
            await Assert.That(change.Data.TryGetProperty("access_after", out _)).IsFalse();
        }
    }

    [Test]
    public async Task Owner_streams_never_share_unfiltered_pages_and_delete_ids_require_matching_preimages()
    {
        await using var api = await CollectionAccessFixture.StartAsync();
        await DeclareAsync(api, OwnerRules());
        var alice = DocumentApi.NewId();
        var bob = DocumentApi.NewId();
        using var first = Reader(api, alice);
        using var second = Reader(api, bob);
        var target = api.DatabasePath() + "/changes";
        using var a = await Stream.OpenAsync(first, target + "?cursor=now");
        using var b = await Stream.OpenAsync(second, target + "?cursor=now");
        await Assert.That((await a.NextAsync()).Name).IsEqualTo("cursor");
        await Assert.That((await b.NextAsync()).Name).IsEqualTo("cursor");
        var hidden = DocumentApi.NewId();
        var own = DocumentApi.NewId();
        (await api.PutAsync(hidden, new { owner = bob, value = "bob" })).Status.ShouldBe(HttpStatusCode.Created);
        (await api.PutAsync(own, new { owner = alice, value = "alice" })).Status.ShouldBe(HttpStatusCode.Created);
        await Assert.That((await a.ChangeAsync()).Data.GetProperty("id").GetString()).IsEqualTo(own);
        await Assert.That((await b.ChangeAsync()).Data.GetProperty("id").GetString()).IsEqualTo(hidden);
        (await api.SendAsync(HttpMethod.Delete, api.Document(hidden))).Status.ShouldBe(HttpStatusCode.OK);
        (await api.SendAsync(HttpMethod.Delete, api.Document(own))).Status.ShouldBe(HttpStatusCode.OK);
        var deleted = await a.ChangeAsync();
        await Assert.That(deleted.Data.GetProperty("id").GetString()).IsEqualTo(own);
        await Assert.That(deleted.Data.GetProperty("op").GetString()).IsEqualTo("delete");
        await Assert.That(deleted.Data.GetProperty("document").ValueKind).IsEqualTo(JsonValueKind.Null);
        var transfer = DocumentApi.NewId();
        (await api.PutAsync(transfer, new { owner = alice })).Status.ShouldBe(HttpStatusCode.Created);
        await Assert.That((await a.ChangeAsync()).Data.GetProperty("id").GetString()).IsEqualTo(transfer);
        (await api.PutAsync(transfer, new { owner = bob })).Status.ShouldBe(HttpStatusCode.OK);
        var departed = await a.ChangeAsync();
        await Assert.That(departed.Data.GetProperty("id").GetString()).IsEqualTo(transfer);
        await Assert.That(departed.Data.GetProperty("op").GetString()).IsEqualTo("delete");
        await Assert.That(departed.Data.GetProperty("document").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task Feed_cursors_bind_identity_roles_and_policy_and_live_policy_changes_terminate_the_stream()
    {
        await using var api = await CollectionAccessFixture.StartAsync();
        var policy = OwnerRules() with { Read = new CollectionAccessPolicy(true, true, ["reader"]) };
        await DeclareAsync(api, policy);
        var actor = DocumentApi.NewId();
        using var first = Reader(api, actor);
        using var second = Reader(api, DocumentApi.NewId());
        using var role = Reader(api, actor, "reader");
        var target = api.DatabasePath() + "/changes?collections=items";
        using var stream = await Stream.OpenAsync(first, target + "&cursor=now");
        var cursor = (await stream.NextAsync()).Id;
        foreach (var client in new[] { second, role })
        {
            using var response = await client.GetAsync(Relative(target + "&cursor=" + Uri.EscapeDataString(cursor)));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Gone);
        }
        await DeclareAsync(api, policy with { Read = new CollectionAccessPolicy(true, false, []) });
        Event terminal;
        do
        {
            terminal = await stream.NextAsync();
        }
        while (terminal.Name == "cursor");
        await Assert.That(terminal.Name).IsEqualTo("error");
        await Assert.That(terminal.Data.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("rules_changed");
        using var old = await first.GetAsync(Relative(target + "&cursor=" + Uri.EscapeDataString(cursor)));
        await Assert.That(old.StatusCode).IsEqualTo(HttpStatusCode.Gone);
    }

    [Test]
    public async Task Public_role_and_default_deny_feeds_never_include_collection_metadata()
    {
        await using var api = await CollectionAccessFixture.StartAsync();
        var actor = DocumentApi.NewId();
        using var user = Reader(api, actor);
        using var role = Reader(api, actor, "reader");
        var target = api.DatabasePath() + "/changes";
        using (var refused = await user.GetAsync(Relative(target)))
        {
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }
        var rules = new CollectionAccessRules(null, new CollectionAccessPolicy(false, false, ["reader"]), new CollectionAccessPolicy(false, false, []));
        await DeclareAsync(api, rules);
        using (var refused = await user.GetAsync(Relative(target)))
        {
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }
        using var wrongCase = Reader(api, actor, "Reader");
        using (var refused = await wrongCase.GetAsync(Relative(target + "?collections=items")))
        {
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }
        using var scopedRole = await Stream.OpenAsync(role, target + "?collections=items&cursor=now");
        _ = await scopedRole.NextAsync();
        using var stream = await Stream.OpenAsync(role, target + "?cursor=now");
        _ = await stream.NextAsync();
        (await api.SendAsync(HttpMethod.Post, api.Collections, new { name = "private" })).Status.ShouldBe(HttpStatusCode.Created);
        var hidden = DocumentApi.NewId();
        (await api.SendAsync(HttpMethod.Put, $"{api.Collections}/private/documents/by-key?id={hidden}", new { secret = "private" })).Status.ShouldBe(HttpStatusCode.Created);
        var visible = DocumentApi.NewId();
        (await api.PutAsync(visible, new { value = "visible" })).Status.ShouldBe(HttpStatusCode.Created);
        var roleItem = await scopedRole.ChangeAsync();
        await Assert.That(roleItem.Data.GetProperty("id").GetString()).IsEqualTo(visible);
        await Assert.That(roleItem.Data.GetRawText().Contains(hidden, StringComparison.Ordinal)).IsFalse();
        Event next;
        do
        {
            next = await stream.NextAsync();
        }
        while (next.Name == "cursor");
        await Assert.That(next.Name).IsEqualTo("error");
        await DeclareAsync(api, rules with { Read = new CollectionAccessPolicy(false, true, []) });
        using var publicStream = await Stream.OpenAsync(user, target);
        _ = await publicStream.NextAsync();
        var item = await publicStream.ChangeAsync();
        await Assert.That(item.Data.GetProperty("collection").GetString()).IsEqualTo("items");
        await Assert.That(item.Data.GetProperty("id").GetString()).IsEqualTo(visible);
        await Assert.That(item.Data.GetRawText().Contains(hidden, StringComparison.Ordinal)).IsFalse();
    }

    private static Uri Relative(string value) => new(value, UriKind.Relative);
    private sealed record Event(string Name, string Id, JsonElement Data);
    private sealed class Stream(HttpResponseMessage response, StreamReader reader) : IDisposable
    {
        public static async Task<Stream> OpenAsync(HttpClient client, string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Relative(path));
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var status = response.StatusCode;
                var body = await response.Content.ReadAsStringAsync();
                response.Dispose();
                throw new InvalidOperationException($"expected SSE 200, received {status}: {body}");
            }
            return new Stream(response, new StreamReader(await response.Content.ReadAsStreamAsync()));
        }
        public async Task<Event> NextAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var name = "";
            var id = "";
            string? data = null;
            while (await reader.ReadLineAsync(deadline.Token) is { } line)
            {
                if (line.Length == 0 && data is not null)
                {
                    using var json = JsonDocument.Parse(data);
                    return new Event(name, id, json.RootElement.Clone());
                }
                if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    name = line[7..];
                }
                if (line.StartsWith("id: ", StringComparison.Ordinal))
                {
                    id = line[4..];
                }
                if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    data = line[6..];
                }
            }
            throw new EndOfStreamException("SSE ended before the expected event");
        }
        public async Task<Event> ChangeAsync()
        {
            while (true)
            {
                var item = await NextAsync();
                if (item.Name == "change")
                {
                    return item;
                }
                if (item.Name == "error")
                {
                    throw new InvalidOperationException(item.Data.GetRawText());
                }
            }
        }
        public void Dispose()
        {
            reader.Dispose();
            response.Dispose();
        }
    }
}
