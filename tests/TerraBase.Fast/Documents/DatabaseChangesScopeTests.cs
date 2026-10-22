using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Documents;
using TerraBase.Features.Replication;

namespace TerraBase.Fast.Documents;

/// <summary>Cursor scope and same-machine prefix changes through real SSE listeners.</summary>
public sealed class DatabaseChangesScopeTests
{
    [Test]
    [Arguments("items", "other", HttpStatusCode.Gone)]
    [Arguments("items,other", "other,items", HttpStatusCode.OK)]
    public async Task A_cursor_requires_the_same_collection_set(string before, string after, HttpStatusCode expected)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartAsync(cancellationToken: token);
        (await api.SendAsync(HttpMethod.Post, api.Collections, new { name = "other" }, cancellationToken: token)).Status.ShouldBe(HttpStatusCode.Created);
        var id = DocumentApi.NewId();
        (await api.SendAsync(HttpMethod.Put, $"{api.Collections}/other/documents/by-key?id={id}", new { value = 1 }, cancellationToken: token)).Status.ShouldBe(HttpStatusCode.Created);
        var path = api.DatabasePath() + "/changes";
        var cursor = await CursorAsync(api.Client, $"{path}?cursor=now&collections={before}", token);
        using var resumed = await api.Client.GetAsync(new Uri($"{path}?cursor={Uri.EscapeDataString(cursor)}&collections={after}", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, token);
        await Assert.That(resumed.StatusCode).IsEqualTo(expected);
        if (expected == HttpStatusCode.Gone)
        {
            await ExpiredAsync(resumed, token);
        }
    }

    [Test]
    public async Task A_same_issuer_cursor_rejects_a_late_event_below_its_delivered_stamp()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartAsync(cancellationToken: token);
        var sink = api.Server.Services.GetRequiredService<IChangeGroupSink>();
        var database = await api.TenantOfAsync(cancellationToken: token);
        var origin = ReplicaId.Of(4518, 27, 1);
        var now = DateTimeOffset.UtcNow;
        await ApplyAsync(DocumentApi.NewId(), FeedCursor.At(now - TimeSpan.FromMinutes(1)), 1);
        var path = api.DatabasePath() + "/changes";
        var cursor = await CursorAsync(api.Client, path + "?cursor=now", token);
        await ApplyAsync(DocumentApi.NewId(), FeedCursor.At(now - TimeSpan.FromMinutes(2)), 2);
        using var resumed = await api.Client.GetAsync(new Uri($"{path}?cursor={Uri.EscapeDataString(cursor)}", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, token);
        await Assert.That(resumed.StatusCode).IsEqualTo(HttpStatusCode.Gone);
        await ExpiredAsync(resumed, token);

        async Task ApplyAsync(string id, Hlc stamp, long sequence)
        {
            using var json = JsonDocument.Parse("""{"value":1}""");
            var state = DocumentState.Bottom.Put(new EntryVersion(stamp, origin), json.RootElement);
            _ = await sink.ApplyAsync(database, origin,
                [new OriginGroup(sequence, [new ChangeDelta(DocumentApi.Collection, id, stamp, state.DeltaAt(stamp))])], token);
        }
    }

    [Test]
    public async Task A_cursor_cannot_resume_another_authorized_database()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartAsync(cancellationToken: token);
        var other = api.DatabasePath("other");
        (await api.SendAsync(HttpMethod.Put, other, cancellationToken: token)).Status.ShouldBe(HttpStatusCode.Created);
        var cursor = await CursorAsync(api.Client, api.DatabasePath() + "/changes?cursor=now", token);
        using var resumed = await api.Client.GetAsync(new Uri($"{other}/changes?cursor={Uri.EscapeDataString(cursor)}", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, token);
        await Assert.That(resumed.StatusCode).IsEqualTo(HttpStatusCode.Gone);
        await ExpiredAsync(resumed, token);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_stolen_or_forged_cursor_cannot_read_another_tenants_database(bool forgeScope)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartAsync(cancellationToken: token);
        using var other = await api.Management.CreateTenantAsync("documents:read", "documents:write");
        var path = $"/v1/projects/{other.ProjectId}/databases/db";
        using var created = await other.Data.PutAsync(new Uri(path, UriKind.Relative), null, token);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var cursor = await CursorAsync(other.Data, path + "/changes?cursor=now", token);
        if (forgeScope)
        {
            cursor = ChangeCursor.Encode(ChangeCursor.Decode(cursor)!, ChangeCursor.Scope(await api.TenantOfAsync(cancellationToken: token), null));
        }

        using var response = await api.Client.GetAsync(new Uri($"{path}/changes?cursor={Uri.EscapeDataString(cursor)}", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        await Assert.That(body.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("unknown_database");
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task A_replica_validates_a_cursor_after_retention_advances(bool atCursor, bool differentHistory)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var source = await DocumentApi.StartAsync(settings => settings["region"] = "gru", cancellationToken: token);
        await using var destination = await DocumentApi.StartAsync(settings => settings["region"] = "sjc", cancellationToken: token);
        var origin = ReplicaId.Of(4518, 28, 1);
        var now = DateTimeOffset.UtcNow;
        var stamps = Enumerable.Range(0, 3).Select(i => FeedCursor.At(now - TimeSpan.FromMinutes(3 - i))).ToArray();
        var ids = Enumerable.Range(0, 3).Select(_ => DocumentApi.NewId()).ToArray();
        foreach (var api in new[] { source, destination })
        {
            await ApplyAsync(api, differentHistory && api == destination ? DocumentApi.NewId() : ids[0], 0);
            await ApplyAsync(api, ids[1], 1);
        }

        var cursor = await CursorAsync(source.Client, source.DatabasePath() + "/changes?cursor=now", token);
        var database = await destination.TenantOfAsync(cancellationToken: token);
        var store = (await destination.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, token))!;
        await using (var trim = await store.BeginTransactionAsync(write: true, token))
        {
            var before = atCursor ? new Hlc(stamps[1].Value + 1) : stamps[1];
            await Assert.That(await trim.TrimFeedAsync(before, 1024, token)).IsEqualTo(atCursor ? 2 : 1);
            _ = await trim.CommitAsync(token);
        }

        await ApplyAsync(destination, ids[2], 2);
        // Separate fixture tenants require a new scope; keep the source's original history proof.
        cursor = ChangeCursor.Encode(ChangeCursor.Decode(cursor)!, ChangeCursor.Scope(database, null));
        using var response = await destination.Client.GetAsync(new Uri($"{destination.DatabasePath()}/changes?cursor={Uri.EscapeDataString(cursor)}", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, token);
        await Assert.That(response.StatusCode).IsEqualTo(differentHistory ? HttpStatusCode.Gone : HttpStatusCode.OK);
        if (differentHistory)
        {
            await ExpiredAsync(response, token);
            return;
        }

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                using var data = JsonDocument.Parse(line[6..]);
                if (data.RootElement.TryGetProperty("id", out var id))
                {
                    await Assert.That(id.GetString()).IsEqualTo(ids[2]);
                    return;
                }
            }
        }

        throw new EndOfStreamException("the resumed stream ended before its next change");

        async Task ApplyAsync(DocumentApi api, string id, int index)
        {
            using var json = JsonDocument.Parse("""{"value":1}""");
            var state = DocumentState.Bottom.Put(new EntryVersion(stamps[index], origin), json.RootElement);
            _ = await api.Server.Services.GetRequiredService<IChangeGroupSink>().ApplyAsync(await api.TenantOfAsync(cancellationToken: token), origin,
                [new OriginGroup(index + 1, [new ChangeDelta(DocumentApi.Collection, id, stamps[index], state.DeltaAt(stamps[index]))])], token);
        }
    }

    private static async Task ExpiredAsync(HttpResponseMessage response, CancellationToken token)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        await Assert.That(body.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("cursor_expired");
    }

    private static async Task<string> CursorAsync(HttpClient client, string path, CancellationToken token)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                return line[4..];
            }
        }

        throw new EndOfStreamException("the changes stream ended before its cursor");
    }
}
