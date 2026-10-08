using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class SubscriptionApiTests
{
    private const string Collection = "items";
    private const string Open = """{"eq":["/status","open"]}""";
    private static readonly string[] MalformedScope = [" shapes:read "];
    private readonly Faker _faker = new();

    [Test]
    public async Task A_subscription_streams_a_filtered_snapshot_then_upserts_filter_leaves_and_deletes()
    {
        await using var api = await ManagementApi.StartAsync();
        using var deadline = Deadline();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, Collection);
        var first = NewId();
        var second = NewId();
        var excluded = NewId();
        var text = _faker.Lorem.Sentence() + "\n\nevent: error\ndata: forged\n<>&";
        // Stored JSON retains real CR/LF whitespace as well as escaped newlines inside string values.
        var raw = "{\r\n  \"status\": \"open\",\n  \"text\": " + JsonSerializer.Serialize(text) + "\n}";
        await PutRawAsync(data, database, first, raw, deadline.Token);
        await PutAsync(data, database, second, "open", deadline.Token);
        var head = await PutAsync(data, database, excluded, "closed", deadline.Token);

        // The token's subject is not a member; there is no organization claim or API key on this client.
        using var client = api.WithToken(Token(api, database));
        using var response = await SubscribeAsync(client, Route(database) + Filter(), deadline.Token);
        await AssertStreamAsync(response);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
        var snapshot = await ReadShapeAsync(reader, deadline.Token);
        await Assert.That(snapshot.GetProperty("offset").GetString()).StartsWith("snap.");
        await Assert.That(snapshot.GetProperty("next_offset").GetString()).IsEqualTo(head);
        await Assert.That(Ops(snapshot)).IsEquivalentTo([$"upsert {first}", $"upsert {second}"]);
        var document = Operations(snapshot).Single(op => op.GetProperty("id").GetString() == first).GetProperty("document");
        await Assert.That(document.GetProperty("text").GetString()).IsEqualTo(text);
        await Assert.That(document.GetRawText().Contains('\n', StringComparison.Ordinal)).IsFalse();

        var updated = await PutAsync(data, database, first, "open", deadline.Token);
        var upsert = await ReadThroughAsync(reader, updated, deadline.Token);
        await AssertContinuationAsync(snapshot, upsert);
        await Assert.That(Ops(upsert)).IsEquivalentTo([$"upsert {first}"]);
        var left = await PutAsync(data, database, first, "closed", deadline.Token);
        var leaving = await ReadThroughAsync(reader, left, deadline.Token);
        await AssertContinuationAsync(upsert, leaving);
        await Assert.That(Ops(leaving)).IsEquivalentTo([$"delete {first}"]);
        using var deleted = await data.DeleteAsync(Document(database, second), deadline.Token);
        deleted.EnsureSuccessStatusCode();
        var deletedOffset = (await deleted.Content.ReadFromJsonAsync<JsonElement>(deadline.Token)).GetProperty("offset").GetString()!;
        var deletion = await ReadThroughAsync(reader, deletedOffset, deadline.Token);
        await AssertContinuationAsync(leaving, deletion);
        await Assert.That(Ops(deletion)).IsEquivalentTo([$"delete {second}"]);
    }

    [Test]
    public async Task Newline_heavy_JSON_has_bounded_SSE_framing_in_snapshots_and_changes()
    {
        await using var api = await ManagementApi.StartAsync();
        using var deadline = Deadline();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, Collection);
        var id = NewId();
        var text = new string('x', 20_000) + "\r\nevent: error\ndata: forged";
        var raw = "{" + new string('\n', 900_000) + "\"text\":" + JsonSerializer.Serialize(text) + "}";
        var offset = await PutRawAsync(data, database, id, raw, deadline.Token);
        using var client = api.WithToken(Token(api, database));
        using var response = await SubscribeAsync(client, Route(database), deadline.Token);
        await AssertStreamAsync(response);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
        var snapshot = await ReadEventAsync(reader, deadline.Token);
        await Assert.That(snapshot).IsNotNull();
        await Assert.That(snapshot!.Type).IsEqualTo("shape");
        // Removing insignificant CR/LF leaves one data line instead of amplifying 900,000 lines of whitespace.
        await Assert.That(snapshot.Data.Length).IsLessThan(25_000);
        await Assert.That(snapshot.Data.Contains('\n', StringComparison.Ordinal)).IsFalse();
        using var json = JsonDocument.Parse(snapshot.Data);
        await Assert.That(json.RootElement.GetProperty("next_offset").GetString()).IsEqualTo(offset);
        await Assert.That(Operations(json.RootElement).Single().GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);

        text += " updated";
        raw = "{\r\n\"text\":" + JsonSerializer.Serialize(text) + "," + new string('\r', 900_000) + "\"updated\":true}";
        offset = await PutRawAsync(data, database, id, raw, deadline.Token);
        var change = await ReadThroughAsync(reader, offset, deadline.Token);
        await Assert.That(change.GetRawText().Length).IsLessThan(25_000);
        await Assert.That(Operations(change).Single().GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);
    }

    [Test]
    public async Task A_document_id_is_combined_with_the_where_filter()
    {
        await using var api = await ManagementApi.StartAsync();
        using var deadline = Deadline();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, Collection);
        var selected = NewId();
        await PutAsync(data, database, selected, "closed", deadline.Token);
        await PutAsync(data, database, NewId(), "open", deadline.Token);
        using var client = api.WithToken(Token(api, database, token => token.Claims["scope"] = "documents:read shapes:read"));
        using var response = await SubscribeAsync(client, Route(database) + Filter() + "&offset=-1&id=" + selected, deadline.Token);
        await AssertStreamAsync(response);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
        var snapshot = await ReadShapeAsync(reader, deadline.Token);
        await Assert.That(Ops(snapshot)).IsEmpty();

        var offset = await PutAsync(data, database, selected, "open", deadline.Token);
        var entered = await ReadThroughAsync(reader, offset, deadline.Token);
        await Assert.That(Ops(entered)).IsEquivalentTo([$"upsert {selected}"]);
        var left = await PutAsync(data, database, selected, "closed", deadline.Token);
        await Assert.That(Ops(await ReadThroughAsync(reader, left, deadline.Token))).IsEquivalentTo([$"delete {selected}"]);
    }

    [Test]
    public async Task Last_event_id_resumes_after_the_snapshot_and_binds_the_filter_and_document_id()
    {
        await using var api = await ManagementApi.StartAsync();
        using var deadline = Deadline();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, Collection);
        var id = NewId();
        await PutAsync(data, database, id, "open", deadline.Token);
        using var client = api.WithToken(Token(api, database, token => token.Claims["database"] = database.Name));
        var route = Route(database) + Filter() + "&id=" + id;
        JsonElement snapshot;
        using (var response = await SubscribeAsync(client, route, deadline.Token))
        {
            await AssertStreamAsync(response);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
            snapshot = await ReadShapeAsync(reader, deadline.Token);
        }

        var offset = await PutAsync(data, database, id, "closed", deadline.Token);
        var lastEventId = snapshot.GetProperty("handle").GetString() + ":" + snapshot.GetProperty("next_offset").GetString();
        using (var resumed = await SubscribeAsync(client, route, lastEventId, deadline.Token))
        {
            await AssertStreamAsync(resumed);
            using var reader = new StreamReader(await resumed.Content.ReadAsStreamAsync(deadline.Token));
            var page = await ReadThroughAsync(reader, offset, deadline.Token);
            await AssertContinuationAsync(snapshot, page);
            await Assert.That(Ops(page)).IsEquivalentTo([$"delete {id}"]);
        }

        await AssertJsonErrorAsync(
            SubscribeAsync(client, Route(database) + "?id=" + id, lastEventId, deadline.Token), HttpStatusCode.Conflict, "must_refetch", deadline.Token);
        await AssertJsonErrorAsync(
            SubscribeAsync(client, Route(database) + Filter() + "&id=" + NewId(), lastEventId, deadline.Token), HttpStatusCode.Conflict, "must_refetch", deadline.Token);
        await AssertJsonErrorAsync(
            SubscribeAsync(client, route + "&handle=h1-0000000000000000&offset=" + snapshot.GetProperty("next_offset").GetString(), deadline.Token),
            HttpStatusCode.Conflict, "must_refetch", deadline.Token);
    }

    [Test]
    public async Task Last_event_id_resumes_an_unfinished_snapshot_before_delivering_later_writes()
    {
        await using var api = await ManagementApi.StartAsync();
        using var deadline = Deadline();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, Collection);
        var ids = Enumerable.Range(0, 2001).Select(_ => NewId()).Order(StringComparer.Ordinal).ToArray();
        var head = "";
        foreach (var batch in ids.Chunk(1024))
        {
            using var written = await data.PostAsJsonAsync(ManagementApi.Uri($"{database.Data}/transactions"), new
            {
                ops = batch.Select(id => new { op = "put", collection = Collection, id, document = new { status = "open" } }).ToArray(),
            }, deadline.Token);
            written.EnsureSuccessStatusCode();
            head = (await written.Content.ReadFromJsonAsync<JsonElement>(deadline.Token)).GetProperty("offset").GetString()!;
        }

        using var client = api.WithToken(Token(api, database));
        var route = Route(database) + Filter();
        SseEvent first;
        JsonElement previous;
        using (var response = await SubscribeAsync(client, route, deadline.Token))
        {
            await AssertStreamAsync(response);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
            first = await ReadEventAsync(reader, deadline.Token) ?? throw new InvalidOperationException("the first snapshot event is missing");
            await Assert.That(first.Type).IsEqualTo("shape");
            using var json = JsonDocument.Parse(first.Data);
            previous = json.RootElement.Clone();
            await Assert.That(Ops(previous)).IsEquivalentTo(ids.Take(1000).Select(id => $"upsert {id}"));
            await Assert.That(previous.GetProperty("next_offset").GetString()).StartsWith($"snap.{head}.");
            await Assert.That(first.Id).IsEqualTo(previous.GetProperty("handle").GetString() + ":" + previous.GetProperty("next_offset").GetString());
        }

        var later = NewId();
        var offset = await PutAsync(data, database, later, "open", deadline.Token);
        var snapshots = new List<string>(Ops(previous));
        using var resumed = await SubscribeAsync(client, route, first.Id, deadline.Token);
        await AssertStreamAsync(resumed);
        using var resumedReader = new StreamReader(await resumed.Content.ReadAsStreamAsync(deadline.Token));
        do
        {
            var page = await ReadShapeAsync(resumedReader, deadline.Token);
            await AssertContinuationAsync(previous, page);
            snapshots.AddRange(Ops(page));
            previous = page;
        }
        while (previous.GetProperty("next_offset").GetString()!.StartsWith("snap.", StringComparison.Ordinal));

        await Assert.That(previous.GetProperty("next_offset").GetString()).IsEqualTo(head);
        await Assert.That(snapshots.Count).IsEqualTo(ids.Length);
        await Assert.That(snapshots).IsEquivalentTo(ids.Select(id => $"upsert {id}"));
        var change = await ReadShapeAsync(resumedReader, deadline.Token);
        await AssertContinuationAsync(previous, change);
        await Assert.That(change.GetProperty("next_offset").GetString()).IsEqualTo(offset);
        await Assert.That(Ops(change)).IsEquivalentTo([$"upsert {later}"]);
    }

    [Test]
    public async Task Subscriptions_reject_absent_credentials_API_keys_and_invalid_JWTs_before_streaming()
    {
        await using var api = await ManagementApi.StartAsync();
        using var deadline = Deadline();
        var database = await api.CreateManagedDatabaseAsync();
        string?[] credentials =
        [
            null,
            await api.Server.IssueAsync(database.Organization, "shapes:read"),
            "not-a-jwt",
            Token(api, database, token => token.Audience = "https://another.test"),
            Token(api, database, token => (token.IssuedAt, token.NotBefore, token.Expires) =
                (DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(-2))),
        ];
        foreach (var credential in credentials)
        {
            using var client = api.WithToken(credential);
            await AssertJsonErrorAsync(SubscribeAsync(client, Route(database), deadline.Token), HttpStatusCode.Unauthorized, null, deadline.Token);
        }
    }

    [Test]
    public async Task Missing_or_wrong_project_and_database_grants_hide_the_database()
    {
        await using var api = await ManagementApi.StartAsync();
        using var deadline = Deadline();
        var database = await api.CreateManagedDatabaseAsync();
        var other = await api.CreateManagedDatabaseAsync("other");
        Action<SecurityTokenDescriptor>[] edits =
        [
            token => token.Claims.Remove("project_id"),
            token => token.Claims["project_id"] = other.Project,
            token => token.Claims["database"] = "other",
        ];
        foreach (var edit in edits)
        {
            using var client = api.WithToken(Token(api, database, edit));
            await AssertJsonErrorAsync(SubscribeAsync(client, Route(database), deadline.Token), HttpStatusCode.NotFound, "unknown_database", deadline.Token);
        }

        using var scoped = api.WithToken(Token(api, database));
        await AssertJsonErrorAsync(SubscribeAsync(scoped, Route(other), deadline.Token), HttpStatusCode.NotFound, "unknown_database", deadline.Token);
        var missing = new ManagedDatabaseRef(database.Organization, database.Project, NewId(), "missing");
        await AssertJsonErrorAsync(SubscribeAsync(scoped, Route(missing), deadline.Token), HttpStatusCode.NotFound, "unknown_database", deadline.Token);
    }

    [Test]
    public async Task Subscriptions_require_the_exact_shapes_read_scope()
    {
        await using var api = await ManagementApi.StartAsync();
        using var deadline = Deadline();
        var database = await api.CreateManagedDatabaseAsync();
        string?[] scopes = [null, "documents:read", "shapes:read-extra"];
        foreach (var scope in scopes)
        {
            using var client = api.WithToken(Token(api, database, token =>
            {
                if (scope is null)
                {
                    token.Claims.Remove("scope");
                }
                else
                {
                    token.Claims["scope"] = scope;
                }
            }));
            await AssertJsonErrorAsync(SubscribeAsync(client, Route(database), deadline.Token), HttpStatusCode.Forbidden, "forbidden_scope", deadline.Token);
        }

        using var malformed = api.WithToken(Token(api, database, token => token.Claims["scope"] = MalformedScope));
        await AssertJsonErrorAsync(SubscribeAsync(malformed, Route(database), deadline.Token), HttpStatusCode.Forbidden, "forbidden_scope", deadline.Token);
    }

    [Test]
    public async Task Invalid_subscription_parameters_are_JSON_errors_before_the_stream_starts()
    {
        await using var api = await ManagementApi.StartAsync();
        using var deadline = Deadline();
        var database = await api.CreateManagedDatabaseAsync();
        using var client = api.WithToken(Token(api, database));
        string[] queries = ["?offset=0", "?offset=invalid", "?where=not-json", "?where=%7B%7D", "?offset=-1&offset=0"];
        foreach (var query in queries)
        {
            await AssertJsonErrorAsync(SubscribeAsync(client, Route(database) + query, deadline.Token), HttpStatusCode.BadRequest, null, deadline.Token);
        }

        await AssertJsonErrorAsync(SubscribeAsync(client, Route(database), "not-an-event-id", deadline.Token), HttpStatusCode.BadRequest, null, deadline.Token);
    }

    [Test]
    public async Task A_collection_dropped_after_start_sends_an_error_event_and_closes_the_stream()
    {
        await using var api = await ManagementApi.StartAsync();
        using var deadline = Deadline();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, Collection);
        await PutAsync(data, database, NewId(), "open", deadline.Token);
        using var client = api.WithToken(Token(api, database));
        using var response = await SubscribeAsync(client, Route(database), deadline.Token);
        await AssertStreamAsync(response);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
        await ReadShapeAsync(reader, deadline.Token);
        using var dropped = await data.DeleteAsync(ManagementApi.Uri($"{database.Data}/collections/{Collection}"), deadline.Token);
        dropped.EnsureSuccessStatusCode();

        SseEvent terminal;
        do
        {
            terminal = await ReadEventAsync(reader, deadline.Token) ?? throw new InvalidOperationException("the SSE stream closed before its error event");
        }
        while (terminal.Type == "shape");

        await Assert.That(terminal.Type).IsEqualTo("error");
        using var error = JsonDocument.Parse(terminal.Data);
        await Assert.That(error.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("unknown_collection");
        await Assert.That(await ReadEventAsync(reader, deadline.Token)).IsNull();
    }

    [Test]
    public async Task The_administrative_listener_rejects_the_client_subscription_route()
    {
        await using var api = await ManagementApi.StartAsync();
        using var deadline = Deadline();
        var database = await api.CreateManagedDatabaseAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, ManagementApi.Uri(Route(database)));
        request.Headers.Authorization = new("Bearer", Token(api, database));
        using var response = await api.Server.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(response.Content.Headers.ContentType?.MediaType == "text/event-stream").IsFalse();
    }

    private string NewId() => _faker.Random.Guid().ToString("D");

    private string Token(ManagementApi api, ManagedDatabaseRef database, Action<SecurityTokenDescriptor>? edit = null) =>
        api.Issuer.Mint(NewId(), token =>
        {
            token.Claims["project_id"] = database.Project;
            token.Claims["scope"] = "shapes:read";
            edit?.Invoke(token);
        });

    private static string Route(ManagedDatabaseRef database) =>
        $"/projects/{database.Project}/databases/{database.Name}/collections/{Collection}/subscribe";

    private static string Filter() => "?where=" + Uri.EscapeDataString(Open);

    private static Uri Document(ManagedDatabaseRef database, string id) =>
        ManagementApi.Uri($"{database.Data}/collections/{Collection}/documents/{id}");

    private static CancellationTokenSource Deadline()
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(30));
        return source;
    }

    private async Task<string> PutAsync(HttpClient data, ManagedDatabaseRef database, string id, string status, CancellationToken cancellationToken) =>
        await PutRawAsync(data, database, id, JsonSerializer.Serialize(new { status, text = _faker.Lorem.Sentence() }), cancellationToken);

    private static async Task<string> PutRawAsync(HttpClient data, ManagedDatabaseRef database, string id, string json, CancellationToken cancellationToken)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await data.PutAsync(Document(database, id), content, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("offset").GetString()!;
    }

    private static Task<HttpResponseMessage> SubscribeAsync(HttpClient client, string route, CancellationToken cancellationToken) =>
        SubscribeAsync(client, route, null, cancellationToken);

    private static async Task<HttpResponseMessage> SubscribeAsync(HttpClient client, string route, string? lastEventId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ManagementApi.Uri(route));
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (lastEventId is not null)
        {
            request.Headers.Add("Last-Event-ID", lastEventId);
        }

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static async Task AssertStreamAsync(HttpResponseMessage response)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/event-stream");
    }

    private static async Task AssertJsonErrorAsync(Task<HttpResponseMessage> sending, HttpStatusCode status, string? code, CancellationToken cancellationToken)
    {
        using var response = await sending;
        await Assert.That(response.StatusCode).IsEqualTo(status);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/json");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var actual = json.RootElement.GetProperty("error").GetProperty("code").GetString();
        await Assert.That(actual).IsNotNull();
        if (code is not null)
        {
            await Assert.That(actual).IsEqualTo(code);
        }
    }

    private static async Task<JsonElement> ReadShapeAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var item = await ReadEventAsync(reader, cancellationToken) ?? throw new InvalidOperationException("the SSE stream closed before a shape event");
        await Assert.That(item.Type).IsEqualTo("shape");
        using var json = JsonDocument.Parse(item.Data);
        var page = json.RootElement.Clone();
        await Assert.That(item.Id).IsEqualTo(page.GetProperty("handle").GetString() + ":" + page.GetProperty("next_offset").GetString());
        await Assert.That(page.GetProperty("handle").GetString()).StartsWith("h1-");
        return page;
    }

    private static async Task<JsonElement> ReadThroughAsync(StreamReader reader, string offset, CancellationToken cancellationToken)
    {
        while (true)
        {
            var page = await ReadShapeAsync(reader, cancellationToken);
            if (page.GetProperty("next_offset").GetString() == offset)
            {
                return page;
            }
        }
    }

    private static async Task AssertContinuationAsync(JsonElement previous, JsonElement next)
    {
        await Assert.That(next.GetProperty("handle").GetString()).IsEqualTo(previous.GetProperty("handle").GetString());
        await Assert.That(next.GetProperty("shape_id").GetString()).IsEqualTo(previous.GetProperty("shape_id").GetString());
        await Assert.That(next.GetProperty("offset").GetString()).IsEqualTo(previous.GetProperty("next_offset").GetString());
        await Assert.That(next.GetProperty("next_offset").GetString()).IsNotEqualTo(previous.GetProperty("next_offset").GetString());
    }

    private static IEnumerable<JsonElement> Operations(JsonElement page) =>
        page.GetProperty("changes").EnumerateArray().SelectMany(change => change.GetProperty("ops").EnumerateArray());

    private static string[] Ops(JsonElement page) =>
        [.. Operations(page).Select(op => op.GetProperty("op").GetString() + " " + op.GetProperty("id").GetString())];

    // Parse actual SSE frames: comments are ignored, every data line contributes a newline, and a blank line dispatches.
    private static async Task<SseEvent?> ReadEventAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var data = new StringBuilder();
        var type = "message";
        string? id = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    return new SseEvent(type, id, data.ToString(0, data.Length - 1));
                }

                type = "message";
                id = null;
                continue;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            var field = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? "" : line[(separator + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            switch (field)
            {
                case "event":
                    type = value;
                    break;
                case "id":
                    id = value;
                    break;
                case "data":
                    data.Append(value).Append('\n');
                    if (data.Length > 8 * 1024 * 1024)
                    {
                        throw new InvalidOperationException("the SSE event exceeds the shape page bound");
                    }

                    break;
            }
        }

        return null;
    }

    private sealed record SseEvent(string Type, string? Id, string Data);
}
