using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Documents;
using TerraBase.Features.Shapes;
using TerraBase.Fast.Documents;
using TerraBase.Testing;

namespace TerraBase.Fast.Shapes;

public sealed class ShapeEndToEndTests
{
    private const string Where = """{"eq":["/status","open"]}""";

    [Test]
    public async Task The_filter_vectors_have_canonical_continuation_urls_and_shared_handles_per_equivalence_group()
    {
        await using var api = await DocumentApi.StartAsync();
        var client = new ShapeClient(api, query: null);
        using var vectors = await LoadVectorsAsync();
        Dictionary<string, string> handles = [with(StringComparer.Ordinal)];
        foreach (var vector in vectors.RootElement.GetProperty("filter_vectors").EnumerateArray())
        {
            var where = vector.GetProperty("input_where_utf8").GetString();
            var target = $"{api.Collections}/{DocumentApi.Collection}/shape?offset=-1"
                + (where is null ? "" : "&where=" + Uri.EscapeDataString(where));
            var answer = await client.Get(target);
            await Assert.That(answer.Status).IsEqualTo(HttpStatusCode.TemporaryRedirect).Because(vector.GetProperty("id").GetString()!);
            var location = answer.Location!.OriginalString;
            var query = location[(location.IndexOf('?') + 1)..].Split('&').Select(pair => pair.Split('=', 2))
                .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);
            await Assert.That(query.GetValueOrDefault("where", "")).IsEqualTo(vector.GetProperty("expected").GetProperty("canonical_filter").GetString());
            var group = vector.GetProperty("equivalence_group").GetString()!;
            if (handles.TryGetValue(group, out var previous))
            {
                await Assert.That(answer.Handle).IsEqualTo(previous);
            }
            else
            {
                handles.Add(group, answer.Handle!);
            }
        }
    }

    [Test]
    [Arguments(1, 64)]
    [Arguments(18, 60 * 1024)]
    public async Task A_production_size_machine_syncs_a_hobby_shape_instead_of_answering_shape_busy(int documents, int bytes)
    {
        await using var api = await DocumentApi.StartAsync(settings => settings["memory-budget"] = "120231936");
        var faker = new Faker();
        HashSet<string> ids = [with(StringComparer.Ordinal)];
        for (var index = 0; index < documents; index++)
        {
            var id = DocumentApi.NewId();
            await Assert.That((await PutAsync(api, id, new { status = "open", body = faker.Random.String2(bytes) })).Status).IsEqualTo(HttpStatusCode.Created);
            ids.Add(id);
        }

        var client = new ShapeClient(api, query: null);
        var head = await client.SyncAsync();

        await Assert.That(head.UpToDate).IsEqualTo("true");
        await Assert.That(client.State.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(ids)).IsTrue()
            .Because("full snapshot pages fit Hobby's share of the production budget");
    }

    [Test]
    public async Task The_http_resume_validation_vectors_return_their_exact_error_bodies_and_cache_headers()
    {
        await using var api = await DocumentApi.StartAsync();
        var client = new ShapeClient(api, query: null);
        using var vectors = await LoadVectorsAsync();
        var cases = vectors.RootElement.GetProperty("http_vectors").EnumerateArray()
            .Where(vector => vector.GetProperty("response").GetProperty("status").GetInt32() == 400
                && vector.GetProperty("request").GetProperty("path").GetString()!.EndsWith("/shape", StringComparison.Ordinal));
        foreach (var vector in cases)
        {
            var request = vector.GetProperty("request");
            var query = string.Join('&', request.GetProperty("query").EnumerateObject()
                .Select(pair => pair.Name + "=" + Uri.EscapeDataString(pair.Value.GetString()!)));
            var headers = request.GetProperty("headers").EnumerateObject()
                .Where(header => header.Name != "Authorization")
                .Select(header => (header.Name, header.Value.GetString()!)).ToArray();
            var answer = await client.Get($"{api.Collections}/{DocumentApi.Collection}/shape?{query}", headers);
            var expected = vector.GetProperty("response");
            await Assert.That((int)answer.Status).IsEqualTo(expected.GetProperty("status").GetInt32())
                .Because(vector.GetProperty("id").GetString()!);
            await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(answer.Json.GetRawText()),
                JsonNode.Parse(expected.GetProperty("body").GetRawText()))).IsTrue();
            await CacheControlAssert.EqualAsync(answer.CacheControl);
        }
    }

    [Test]
    public async Task A_client_syncs_snapshot_then_chunks_then_parks_live_and_converges_on_writes_and_deletes()
    {
        await using var api = await DocumentApi.StartAsync();
        var faker = new Faker();
        for (var index = 0; index < 5; index++)
        {
            await PutAsync(api, DocumentApi.NewId(), new { title = faker.Lorem.Word(), status = "open" });
        }

        var client = new ShapeClient(api, query: null);
        await client.SyncAsync();
        await AssertConvergedAsync(api, client, query: null);

        var chunkId = DocumentApi.NewId();
        await PutAsync(api, chunkId, new { title = "chunk", status = "open" });
        var chunk = await client.ReadAsync(client.NextOffset);
        await Assert.That(chunk.UpToDate).IsEqualTo("false");
        await Assert.That(Ops(chunk).Select(op => op.GetProperty("id").GetString())).Contains(chunkId);
        await AssertConvergedAsync(api, client, query: null);
        var parked = client.LiveAsync(client.NextOffset);
        await ParkedAsync(api, readers: 1);
        var written = DocumentApi.NewId();
        await Assert.That((await PutAsync(api, written, new { title = "live", status = "open" })).Status).IsEqualTo(HttpStatusCode.Created);
        var answer = await parked;
        await Assert.That(answer.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(answer.UpToDate).IsEqualTo("true");
        await Assert.That(Ops(answer).Select(op => op.GetProperty("id").GetString())).Contains(written);
        await AssertConvergedAsync(api, client, query: null);

        var parkedAgain = client.LiveAsync(client.NextOffset);
        await ParkedAsync(api, readers: 1);
        var deleted = await api.SendAsync(HttpMethod.Delete, api.Document(written));
        await Assert.That(deleted.Status).IsEqualTo(HttpStatusCode.OK);
        var removal = await parkedAgain;
        await Assert.That(Ops(removal).Single(op => op.GetProperty("id").GetString() == written).GetProperty("op").GetString()).IsEqualTo("delete");
        await AssertConvergedAsync(api, client, query: null);
    }

    [Test]
    public async Task A_filtered_shape_converges_across_transitions_out_of_the_filter_and_deletes()
    {
        await using var api = await DocumentApi.StartAsync();
        var moving = DocumentApi.NewId();
        var removed = DocumentApi.NewId();
        var version = (await PutAsync(api, moving, new { status = "open" })).Version;
        await PutAsync(api, removed, new { status = "open" });
        await PutAsync(api, DocumentApi.NewId(), new { status = "closed" });

        var client = new ShapeClient(api, Where);
        await client.SyncAsync();
        await AssertConvergedAsync(api, client, Where);
        var parked = client.LiveAsync(client.NextOffset);
        await ParkedAsync(api, readers: 1);
        await Assert.That((await PutAsync(api, moving, new { status = "closed" }, DocumentApi.IfMatch(version))).Status).IsEqualTo(HttpStatusCode.OK);
        var transition = await parked;
        await Assert.That(Ops(transition).Single(op => op.GetProperty("id").GetString() == moving).GetProperty("op").GetString()).IsEqualTo("delete");
        await AssertConvergedAsync(api, client, Where);

        var parkedAgain = client.LiveAsync(client.NextOffset);
        await ParkedAsync(api, readers: 1);
        var deleted = await api.SendAsync(HttpMethod.Delete, api.Document(removed));
        await Assert.That(deleted.Status).IsEqualTo(HttpStatusCode.OK);
        var removal = await parkedAgain;
        await Assert.That(Ops(removal).Single(op => op.GetProperty("id").GetString() == removed).GetProperty("op").GetString()).IsEqualTo("delete");
        await AssertConvergedAsync(api, client, Where);
    }

    [Test]
    public async Task A_repeated_chunk_and_snapshot_page_answer_304_with_the_same_headers_and_no_body()
    {
        await using var api = await DocumentApi.StartAsync();
        await PutAsync(api, DocumentApi.NewId(), new { status = "open" });
        var client = new ShapeClient(api, query: null);
        var bootstrap = await client.Get($"{api.Collections}/{DocumentApi.Collection}/shape?offset=-1");
        await Assert.That(bootstrap.Status).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        await CacheControlAssert.EqualAsync(bootstrap.CacheControl);
        var page = await client.Get(bootstrap.Location!.OriginalString);
        await Assert.That(page.Status).IsEqualTo(HttpStatusCode.OK);
        await AssertWireBodyAsync(page);

        var cached = await client.Get(page.Uri!, ("If-None-Match", page.ETag!));
        await AssertNotModifiedAsync(cached, page);
        await client.SyncAsync();
        var closedAt = client.NextOffset;
        await PutAsync(api, DocumentApi.NewId(), new { status = "open" });
        var chunk = await client.Get(client.Uri(closedAt, live: false));
        await Assert.That(chunk.Status).IsEqualTo(HttpStatusCode.OK);
        await AssertWireBodyAsync(chunk);
        var chunkCached = await client.Get(chunk.Uri!, ("If-None-Match", chunk.ETag!));
        await AssertNotModifiedAsync(chunkCached, chunk);
        await PutAsync(api, DocumentApi.NewId(), new { status = "open" });
        var again = await client.Get(chunk.Uri!);
        await Assert.That(again.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(again.Body.AsSpan().SequenceEqual(chunk.Body)).IsTrue().Because("a sealed chunk never recomputes");
        await Assert.That(again.ETag).IsEqualTo(chunk.ETag);
        await AssertNotModifiedAsync(await client.Get(chunk.Uri!, ("If-None-Match", chunk.ETag!)), chunk);
    }

    private static async Task AssertNotModifiedAsync(Reply cached, Reply original)
    {
        await Assert.That(cached.Status).IsEqualTo(HttpStatusCode.NotModified);
        await Assert.That(cached.Body.Length).IsEqualTo(0);
        await Assert.That(cached.ETag).IsEqualTo(original.ETag);
        await Assert.That(cached.Handle).IsEqualTo(original.Handle);
        await Assert.That(cached.NextOffset).IsEqualTo(original.NextOffset);
        await CacheControlAssert.EqualAsync(cached.CacheControl);
        await Assert.That(cached.Vary).Contains("Authorization");
    }

    private static async Task AssertWireBodyAsync(Reply reply)
    {
        string[] members = ["shape_id", "handle", "offset", "next_offset", "covered_offset", "values_at", "up_to_date", "cursor_resume", "changes"];
        await Assert.That(reply.Json.EnumerateObject().Select(member => member.Name).SequenceEqual(members)).IsTrue();
        var digest = Convert.ToHexStringLower(SHA256.HashData(reply.Body).AsSpan(0, 8));
        var body = reply.Json;
        var tag = $"\"{body.GetProperty("shape_id").GetString()}:{reply.Handle}:{body.GetProperty("offset").GetString()}:{reply.NextOffset}:{digest}\"";
        await Assert.That(reply.ETag).IsEqualTo(tag);
        var cursor = Base64UrlDecode(body.GetProperty("cursor_resume").GetString()!);
        await Assert.That(cursor[0]).IsEqualTo((byte)1);
        await Assert.That(ChangeCursor.Decode(System.Buffers.Text.Base64Url.EncodeToString(cursor.AsSpan(17)))).IsNotNull();
        await Assert.That(reply.ContentType).IsEqualTo("application/json");
        await CacheControlAssert.EqualAsync(reply.CacheControl);
        await Assert.That(reply.Vary).Contains("Authorization");
    }

    [Test]
    public async Task Fresh_bodies_carry_the_reached_r8_cursor_and_plaintext_metadata_headers()
    {
        await using var api = await DocumentApi.StartAsync();
        await PutAsync(api, DocumentApi.NewId(), new { status = "open" });
        var client = new ShapeClient(api, query: null);
        var synced = await client.SyncAsync();

        var at = await client.Get(client.Uri(synced.NextOffset!, live: false));

        await Assert.That(at.Status).IsEqualTo(HttpStatusCode.OK);
        await AssertWireBodyAsync(at);
        await Assert.That(at.UpToDate).IsEqualTo("true");
        await Assert.That(at.NextOffset).IsEqualTo(synced.NextOffset);
        var envelope = Base64UrlDecode(at.Json.GetProperty("cursor_resume").GetString()!);
        await Assert.That(envelope[0]).IsEqualTo((byte)1);
        var tenant = await api.TenantOfAsync();
        var collection = await api.SendAsync(HttpMethod.Get, api.Collections + "/" + DocumentApi.Collection);
        await Assert.That(collection.Status).IsEqualTo(HttpStatusCode.OK);
        var fields = string.Join('\0', tenant.OrganizationId, tenant.ProjectId, tenant.DatabaseId,
            DocumentApi.Collection, collection.Json.GetProperty("id").GetString(), "", "1");
        var expectedScope = SHA256.HashData(Encoding.UTF8.GetBytes("TB-SHAPE-RESUME-V1\0" + fields));
        await Assert.That(envelope.AsSpan(1, 16).SequenceEqual(expectedScope.AsSpan(0, 16))).IsTrue()
            .Because("the HTTP resume scope binds the tenant, collection generation, filter and wire version exactly");
        var reached = ChangeCursor.Decode(System.Buffers.Text.Base64Url.EncodeToString(envelope.AsSpan(17)));
        await Assert.That(reached).IsNotNull();
        var head = await FeedHeadAsync(api);
        await Assert.That(reached!.Stamps.Count).IsGreaterThan(0);
        await Assert.That(reached.Stamps).IsEquivalentTo(head.Stamps);
        await Assert.That(at.Body[0]).IsEqualTo((byte)'{');
        await Assert.That(at.ContentType).IsEqualTo("application/json");
    }

    [Test]
    public async Task A_collection_recreated_under_a_client_rotates_the_handle_and_refuses_the_old_one_with_409()
    {
        await using var api = await DocumentApi.StartAsync();
        await PutAsync(api, DocumentApi.NewId(), new { status = "open" });
        var client = new ShapeClient(api, query: null);
        var synced = await client.SyncAsync();
        var oldHandle = synced.Handle;

        var dropped = await api.SendAsync(HttpMethod.Delete, api.Collections + "/" + DocumentApi.Collection);
        await Assert.That(dropped.Status).IsEqualTo(HttpStatusCode.OK);
        var created = await api.SendAsync(HttpMethod.Post, api.Collections, new { name = DocumentApi.Collection });
        await Assert.That(created.Status).IsEqualTo(HttpStatusCode.Created);
        await PutAsync(api, DocumentApi.NewId(), new { status = "open" });

        var stale = await client.Get(client.Uri(synced.NextOffset!, live: false, handle: oldHandle));

        await Assert.That(stale.Status).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(stale.Code).IsEqualTo("must_refetch");
        await CacheControlAssert.EqualAsync(stale.CacheControl);
        await Assert.That(stale.Handle).IsNotEqualTo(oldHandle);
        await Assert.That(stale.Json.GetProperty("error").GetProperty("handle").GetString()).IsEqualTo(stale.Handle);
    }

    [Test]
    public async Task A_parked_live_reader_refetches_when_its_collection_is_recreated()
    {
        await using var api = await DocumentApi.StartAsync();
        await PutAsync(api, DocumentApi.NewId(), new { status = "open" });
        var client = new ShapeClient(api, query: null);
        var synced = await client.SyncAsync();
        var parked = client.LiveAsync(client.NextOffset);
        await ParkedAsync(api, readers: 1);

        await api.SendAsync(HttpMethod.Delete, api.Collections + "/" + DocumentApi.Collection);
        await api.SendAsync(HttpMethod.Post, api.Collections, new { name = DocumentApi.Collection });
        await PutAsync(api, DocumentApi.NewId(), new { status = "open" });

        var stale = await parked;
        await Assert.That(stale.Status is HttpStatusCode.Conflict or HttpStatusCode.NotFound).IsTrue();
        await Assert.That(stale.Code).IsEqualTo(stale.Status == HttpStatusCode.Conflict ? "must_refetch" : "unknown_collection");
        await CacheControlAssert.EqualAsync(stale.CacheControl);
        var bootstrap = await client.Get($"{api.Collections}/{DocumentApi.Collection}/shape?offset=-1");
        await Assert.That(bootstrap.Handle).IsNotEqualTo(synced.Handle);
    }

    [Test]
    public async Task Retention_refuses_an_old_offset_and_bootstrap_uses_a_snapshot_at_or_above_the_floor()
    {
        await using var api = await DocumentApi.StartAsync();
        await PutAsync(api, DocumentApi.NewId(), new { status = "open" });
        var client = new ShapeClient(api, query: null);
        var initial = await client.Get($"{api.Collections}/{DocumentApi.Collection}/shape?offset=-1");
        await client.SyncAsync();
        var oldOffset = client.NextOffset;
        await PutAsync(api, DocumentApi.NewId(), new { status = "open" });
        await client.ReadAsync(oldOffset);
        var token = TestContext.Current!.Execution.CancellationToken;
        var store = (await api.Server.Services.GetRequiredService<TursoStorageEngine>()
            .OpenDatabaseAsync(await api.TenantOfAsync(), token))!;
        var retention = new ShapeRetention(new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(8)),
            api.Server.Services.GetRequiredService<IShapeStorage>());
        await Assert.That(await retention.RunPassAsync(store, client.Handle!, token)).IsGreaterThan(0);
        var floor = await retention.GetFloorAsync(store, client.Handle!, token);

        var stale = await client.Get(client.Uri(oldOffset, live: false));
        await Assert.That(stale.Status).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(stale.Code).IsEqualTo("must_refetch");
        await CacheControlAssert.EqualAsync(stale.CacheControl);

        var stalePage = await client.Get(initial.Location!.OriginalString);
        await Assert.That(stalePage.Status).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(stalePage.Code).IsEqualTo("must_refetch");

        var replacement = new ShapeClient(api, query: null);
        await replacement.SyncAsync();
        await Assert.That(long.Parse(replacement.NextOffset, CultureInfo.InvariantCulture)).IsGreaterThanOrEqualTo(floor);
        await AssertConvergedAsync(api, replacement, query: null);
    }

    [Test]
    public async Task An_oversized_shape_does_not_fail_another_collections_parked_reader()
    {
        await using var api = await DocumentApi.StartAsync();
        await api.SendAsync(HttpMethod.Post, api.Collections, new { name = "large" });
        var client = new ShapeClient(api, query: null);
        var admitted = await client.Get($"{api.Collections}/large/shape?offset=-1");
        await Assert.That(admitted.Status).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        var ids = Enumerable.Range(0, 10).Select(_ => DocumentApi.NewId()).ToArray();
        var batch = await api.SendAsync(HttpMethod.Post, api.Transactions,
            new { ops = ids.Select(id => new { op = "put", collection = "large", id, document = new { value = "small" } }) });
        await Assert.That(batch.Status).IsEqualTo(HttpStatusCode.OK);
        var large = new Faker().Random.String2(900_000);
        foreach (var id in ids)
        {
            var path = $"{api.Collections}/large/documents/by-key?id={id}";
            var previous = await api.SendAsync(HttpMethod.Get, path);
            var updated = await api.SendAsync(HttpMethod.Put, path, new { value = large }, DocumentApi.IfMatch(previous.Version));
            await Assert.That(updated.Status).IsEqualTo(HttpStatusCode.OK);
        }

        await client.SyncAsync();
        var parked = client.LiveAsync(client.NextOffset);
        await ParkedAsync(api, readers: 1);
        var written = DocumentApi.NewId();
        await PutAsync(api, written, new { status = "open" });
        var answer = await parked;
        await Assert.That(answer.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Ops(answer).Select(op => op.GetProperty("id").GetString())).Contains(written);
        await AssertConvergedAsync(api, client, query: null);
    }

    [Test]
    public async Task Parked_live_readers_share_one_seal_and_one_payload_read()
    {
        await using var api = await DocumentApi.StartAsync();
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        await PutAsync(api, DocumentApi.NewId(), new { status = "open" });
        var client = new ShapeClient(api, query: null);
        var synced = await client.SyncAsync();
        var token = TestContext.Current!.Execution.CancellationToken;
        var first = await WaveAsync(api, client, synced.NextOffset!, readers: 1, token);
        var second = await WaveAsync(api, client, first.NextOffset, readers: 64, token);

        await Assert.That(first.Seals).IsEqualTo(1d).Because("one commit wakes one shared flight");
        await Assert.That(first.PayloadReads).IsEqualTo(1d).Because("one stored payload is read for the flight");
        await Assert.That(second.Seals).IsEqualTo(1d).Because("64 parked readers still share one seal");
        await Assert.That(second.PayloadReads).IsEqualTo(1d).Because("64 parked readers still share one payload read");
        await Assert.That(second.Bodies.Distinct().Count()).IsEqualTo(1).Because("every reader receives the same bytes");
    }

    [Test]
    public async Task Readers_of_a_closed_chunk_one_after_another_share_one_payload_read()
    {
        await using var api = await DocumentApi.StartAsync();
        await PutAsync(api, DocumentApi.NewId(), new { status = "open" });
        var client = new ShapeClient(api, query: null);
        await client.SyncAsync();
        var closedAt = client.NextOffset!;
        await PutAsync(api, DocumentApi.NewId(), new { status = "open" });
        await PutAsync(api, DocumentApi.NewId(), new { status = "open" });
        var token = TestContext.Current!.Execution.CancellationToken;
        var first = await client.Get(client.Uri(closedAt, live: false));
        await Assert.That(first.Status).IsEqualTo(HttpStatusCode.OK);
        var before = await Scrape(api.Server, token);
        for (var reader = 0; reader < 8; reader++)
        {
            var answer = await client.Get(client.Uri(closedAt, live: false));
            await Assert.That(answer.Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(answer.Body.AsSpan().SequenceEqual(first.Body)).IsTrue().Because("a closed chunk answers the same bytes");
        }

        var after = await Scrape(api.Server, token);
        await Assert.That(Delta(before, after, "terrabase_storage_reads_total", "path=\"shape_body\"")).IsEqualTo(0d)
            .Because("the first read retained the closed chunk; later readers read no payload");
    }

    [Test]
    public async Task Concurrent_identical_snapshot_page_reads_share_one_flight_and_one_payload_read()
    {
        var gate = new PageGate();
        await using var api = await DocumentApi.StartAsync(services: collection => Gate(collection, gate));
        await PutAsync(api, DocumentApi.NewId(), new { status = "open" });
        var client = new ShapeClient(api, query: null);
        var bootstrap = await client.Get($"{api.Collections}/{DocumentApi.Collection}/shape?offset=-1");
        await Assert.That(bootstrap.Status).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        var page = bootstrap.Location!.OriginalString;
        var token = TestContext.Current!.Execution.CancellationToken;
        var before = await Scrape(api.Server, token);
        gate.Arm(readers: 4);
        Task<Reply>[] readers = [.. Enumerable.Range(0, 4).Select(_ => client.Get(page))];
        var joinedFirst = await Task.WhenAny(gate.Joined, Task.WhenAny(readers)) == gate.Joined;
        gate.Release();
        var answers = await Task.WhenAll(readers);
        var after = await Scrape(api.Server, token);

        await Assert.That(joinedFirst).IsTrue().Because("no reader answers before all four joined the held flight");
        foreach (var answer in answers)
        {
            await Assert.That(answer.Status).IsEqualTo(HttpStatusCode.OK);
        }
        await Assert.That(answers.Select(answer => Encoding.UTF8.GetString(answer.Body)).Distinct().Count()).IsEqualTo(1).Because("every reader receives the same bytes");
        await Assert.That(gate.PageReads).IsEqualTo(1).Because("one flight reads the page for every reader");
        await Assert.That(Delta(before, after, "terrabase_storage_reads_total", "path=\"shape_body\"")).IsEqualTo(1d)
            .Because("one page payload SELECT serves all four readers");
    }

    [Test]
    public async Task A_reader_fan_out_after_a_catalog_commit_reads_the_catalog_once_instead_of_once_per_reader()
    {
        await using var api = await DocumentApi.StartAsync();
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        await PutAsync(api, DocumentApi.NewId(), new { status = "open" });
        var client = new ShapeClient(api, query: null);
        await client.SyncAsync();
        var closedAt = client.NextOffset!;
        await PutAsync(api, DocumentApi.NewId(), new { status = "open" });
        await PutAsync(api, DocumentApi.NewId(), new { status = "open" });
        await Assert.That((await client.Get(client.Uri(closedAt, live: false))).Status).IsEqualTo(HttpStatusCode.OK);
        var token = TestContext.Current!.Execution.CancellationToken;
        _ = await api.Management.CreateOrganizationAsync("Invalidate the catalog caches");
        var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        var before = await Scrape(api.Server, token);
        var commits = system.CommitSequence;
        var answers = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => client.Get(client.Uri(closedAt, live: false))));
        commits = system.CommitSequence - commits;
        var after = await Scrape(api.Server, token);

        await Assert.That(answers.All(answer => answer.Status == HttpStatusCode.OK)).IsTrue();
        var reads = Delta(before, after, "terrabase_storage_reads_total", "path=\"point\"");
        TestContext.Current!.OutputWriter.WriteLine($"point_reads={reads} catalog_commits={commits}");
        await Assert.That(reads).IsLessThan(60d + (20d * commits));
    }

    [Test]
    public async Task The_metrics_scrape_retains_all_five_storage_paths_after_feed_query_and_shape_reads()
    {
        await using var api = await DocumentApi.StartAsync();
        var token = TestContext.Current!.Execution.CancellationToken;
        var id = DocumentApi.NewId();
        await PutAsync(api, id, new { status = "open" });
        await Assert.That((await api.GetAsync(id, cancellationToken: token)).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await api.QueryAsync(new { limit = 10 }, token)).Status).IsEqualTo(HttpStatusCode.OK);
        using var feed = await api.Client.GetAsync($"{api.DatabasePath()}/changes?cursor=now&collections={DocumentApi.Collection}",
            HttpCompletionOption.ResponseHeadersRead, token);
        await Assert.That(feed.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var client = new ShapeClient(api, query: null);
        await client.SyncAsync();
        await PutAsync(api, DocumentApi.NewId(), new { status = "open" });
        await Assert.That((await client.ReadAsync(client.NextOffset)).Status).IsEqualTo(HttpStatusCode.OK);

        var scrape = await Scrape(api.Server, token);
        string[] paths = ["point", "query", "feed", "shape", "shape_body"];
        foreach (var path in paths)
        {
            await Assert.That(Total(scrape, "terrabase_storage_reads_total", $"path=\"{path}\"")).IsGreaterThan(0d);
        }
        var storage = string.Join('\n', scrape.Split('\n').Where(line => line.StartsWith("terrabase_storage_reads_total", StringComparison.Ordinal)));
        await Assert.That(storage).DoesNotContain("otel_metric_overflow");
    }

    private static async Task AssertConvergedAsync(DocumentApi api, ShapeClient client, string? query)
    {
        var truth = await Truth(api, query);
        await Assert.That(client.State.Keys.Order(StringComparer.Ordinal).SequenceEqual(truth.Keys.Order(StringComparer.Ordinal))).IsTrue()
            .Because("the client holds exactly the ids the collection matches");
        foreach (var (id, document) in truth)
        {
            await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(client.State[id].GetRawText()), JsonNode.Parse(document.GetRawText()))).IsTrue()
                .Because($"document {id} matches the collection");
        }
    }

    private static async Task<Dictionary<string, JsonElement>> Truth(DocumentApi api, string? query)
    {
        var response = await api.SendRawAsync(HttpMethod.Post, api.Query, query is null ? "{}" : $$"""{"filter":{{query}}}""", "application/json");
        return response.Json.GetProperty("items").EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetString()!, item => item.GetProperty("document").Clone(), StringComparer.Ordinal);
    }

    private static async Task<TerraBase.Engine.FeedCursor> FeedHeadAsync(DocumentApi api)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri($"{api.DatabasePath()}/changes?cursor=now&collections={DocumentApi.Collection}", UriKind.Relative));
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await api.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
        for (var line = 0; line < 64; line++)
        {
            if (await reader.ReadLineAsync(token) is { } value && value.StartsWith("id: ", StringComparison.Ordinal))
            {
                return ChangeCursor.Decode(value[4..]) ?? throw new InvalidDataException("the feed returned an invalid cursor");
            }
        }
        throw new InvalidDataException("the feed did not return its head cursor");
    }

    private static async Task<JsonDocument> LoadVectorsAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TerraScale.slnx")))
        {
            root = root.Parent;
        }
        var path = Path.Combine(root?.FullName ?? throw new InvalidOperationException("the repository root holds TerraScale.slnx"),
            "docs", "terrabase", "shape-vectors.json");
        await using var file = File.OpenRead(path);
        return await JsonDocument.ParseAsync(file);
    }

    private static async Task<PutResult> PutAsync(DocumentApi api, string id, object document, Action<HttpRequestMessage>? configure = null)
    {
        var reply = await api.PutAsync(id, document, configure);
        return new PutResult(reply.Status, reply.Version);
    }

    private static async Task<Wave> WaveAsync(DocumentApi api, ShapeClient client, string offset, int readers, CancellationToken token)
    {
        var server = api.Server;
        var before = await Scrape(server, token);
        var readersAt = client.Uri(offset, live: true).OriginalString;
        List<Task<Reply>> waiting = [];
        for (var index = 0; index < readers; index++)
        {
            waiting.Add(client.Get($"{readersAt}&cursor={index}"));
            await TerraBaseTestServer.EventuallyAsync(async ct =>
                waiting.Any(task => task.IsCompleted) || Sample(await Scrape(server, ct), "terrabase_shape_requests_waiting") >= waiting.Count);
            foreach (var completed in waiting.Where(task => task.IsCompleted))
            {
                var early = await completed;
                throw new InvalidOperationException($"reader answered before all {readers} parked: {early.Status}, {Encoding.UTF8.GetString(early.Body)}");
            }
        }
        var written = await PutAsync(api, DocumentApi.NewId(), new { status = "open" });
        await Assert.That(written.Status).IsEqualTo(HttpStatusCode.Created);
        var answers = await Task.WhenAll(waiting);
        foreach (var answer in answers)
        {
            await Assert.That(answer.Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(answer.UpToDate).IsEqualTo("true");
        }
        var after = await Scrape(server, token);
        return new Wave(
            [.. answers.Select(answer => answer.Status == HttpStatusCode.OK ? Encoding.UTF8.GetString(answer.Body) : answer.Status.ToString())],
            answers.First(answer => answer.Status == HttpStatusCode.OK).NextOffset!,
            Delta(before, after, "terrabase_shape_seals_total", null),
            Delta(before, after, "terrabase_storage_reads_total", "path=\"shape_body\""));
    }

    private static void Gate(IServiceCollection services, PageGate gate)
    {
        services.AddSingleton<IShapeFlights<ShapeRead>>(provider => new JoiningFlights(new ShapeFlights<ShapeRead>(
            provider.GetRequiredService<TerraBase.Engine.IStorageEngine>(), provider.GetRequiredService<IShapeFlightSealer>(),
            provider.GetRequiredService<TerraBase.Features.Memory.IMemoryBudget>(), provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(), provider.GetRequiredService<IShapeMetrics>()), gate));
        services.AddSingleton<IShapeSnapshotSealer>(provider => new HeldPages(new ShapeSnapshots(
            provider.GetRequiredService<TerraBase.Features.Memory.IMemoryBudget>(), provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IShapeSnapshotTransactions>(),
            provider.GetRequiredService<IShapeMetrics>()), gate));
    }

    private static Task ParkedAsync(DocumentApi api, int readers) =>
        TerraBaseTestServer.EventuallyAsync(async ct => Sample(await Scrape(api.Server, ct), "terrabase_shape_requests_waiting") >= readers);

    private static IEnumerable<JsonElement> Ops(Reply answer) =>
        answer.Json.GetProperty("changes").EnumerateArray().SelectMany(change => change.GetProperty("ops").EnumerateArray());

    private static double Delta(string before, string after, string name, string? label) => Total(after, name, label) - Total(before, name, label);

    private static double Total(string scrape, string name, string? label) => scrape.Split('\n')
        .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#') && MetricName(line) == name && (label is null || line.Contains(label, StringComparison.Ordinal)))
        .Sum(line => double.Parse(line.Split(' ')[^1], CultureInfo.InvariantCulture));

    private static double Sample(string scrape, string name) => Total(scrape, name, null);

    private static string MetricName(string line) => line[..line.IndexOfAny(['{', ' '])];

    private static Task<string> Scrape(TerraBaseTestServer server, CancellationToken token = default) =>
        server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative), token);

    private static byte[] Base64UrlDecode(string token) => System.Buffers.Text.Base64Url.DecodeFromChars(token);

    private sealed record PutResult(HttpStatusCode Status, string Version);

    private sealed record Wave(string[] Bodies, string NextOffset, double Seals, double PayloadReads);
    private sealed class PageGate
    {
        private readonly TaskCompletionSource _joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readers;
        private int _admitted;
        private int _pageReads;

        public Task Joined => _joined.Task;

        public bool Armed => Volatile.Read(ref _readers) > 0;

        public int PageReads => Volatile.Read(ref _pageReads);

        public void Arm(int readers) => Volatile.Write(ref _readers, readers);

        public void Admit()
        {
            var readers = Volatile.Read(ref _readers);
            if (readers > 0 && Interlocked.Increment(ref _admitted) == readers)
            {
                _joined.TrySetResult();
            }
        }
        public Task HoldAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _pageReads) > 1)
            {
                Release();
            }

            return Joined.WaitAsync(cancellationToken);
        }

        public void Release() => _joined.TrySetResult();
    }

    private sealed class JoiningFlights(IShapeFlights<ShapeRead> inner, PageGate gate) : IShapeFlights<ShapeRead>
    {
        public long NextCursor(long? input) => inner.NextCursor(input);

        public ValueTask<ShapeFlightResult<ShapeRead>> ReadAsync(
            ShapeFlightRequest request, ShapeHeadProbe probe, ShapeTailReader<ShapeRead> read, CancellationToken cancellationToken) =>
            inner.ReadAsync(request.Live ? request : request with { OnAdmitted = gate.Admit }, probe, read, cancellationToken);
    }

    private sealed class HeldPages(IShapeSnapshotSealer inner, PageGate gate) : IShapeSnapshotSealer
    {
        public ValueTask<ShapeSnapshot?> NewestAsync(TerraBase.Engine.IDatabaseStore store, string handle, CancellationToken cancellationToken) =>
            inner.NewestAsync(store, handle, cancellationToken);

        public ValueTask<ShapeSnapshot> SealIfDueAsync(TerraBase.Engine.IDatabaseStore store, ShapeSnapshotDefinition shape, CancellationToken cancellationToken) =>
            inner.SealIfDueAsync(store, shape, cancellationToken);

        public async ValueTask<ShapeSnapshotPage?> ReadPageAsync(
            TerraBase.Engine.IDatabaseStore store, string handle, long offset, string? afterId, CancellationToken cancellationToken)
        {
            if (gate.Armed)
            {
                await gate.HoldAsync(cancellationToken);
            }

            return await inner.ReadPageAsync(store, handle, offset, afterId, cancellationToken);
        }
    }

    private sealed record Reply(HttpStatusCode Status, byte[] Body, JsonElement Json, string? Handle, string? ETag, string? CacheControl,
        string? Vary, string? NextOffset, string? UpToDate, string? ContentType, Uri? Location, Uri? Uri)
    {
        public string? Code => Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty("error", out var error)
            ? error.GetProperty("code").GetString()
            : null;
    }

    private sealed class ShapeClient(DocumentApi api, string? query)
    {
        private readonly HttpClient _http = api.Client;

        public Dictionary<string, JsonElement> State { get; } = [with(StringComparer.Ordinal)];

        public string NextOffset { get; private set; } = "0";

        public string? Handle { get; private set; }

        public async Task<Reply> SyncAsync()
        {
            var bootstrap = await Get($"{api.Collections}/{DocumentApi.Collection}/shape?offset=-1" + Where(query));
            if (bootstrap.Status != HttpStatusCode.TemporaryRedirect)
            {
                throw new InvalidOperationException($"bootstrap answered {bootstrap.Status}");
            }
            Handle = bootstrap.Handle;
            var page = await Get(bootstrap.Location!.OriginalString);
            Apply(page);
            while (NextOffset.StartsWith("snap.", StringComparison.Ordinal))
            {
                Apply(await Get(Uri(NextOffset, live: false)));
            }
            Reply chunk;
            do
            {
                chunk = await Get(Uri(NextOffset, live: false));
                Apply(chunk);
            }
            while (chunk.UpToDate != "true");
            return chunk;
        }

        public async Task<Reply> ReadAsync(string offset)
        {
            var reply = await Get(Uri(offset, live: false));
            Apply(reply);
            return reply;
        }
        public async Task<Reply> LiveAsync(string offset)
        {
            var reply = await Get(Uri(offset, live: true));
            Apply(reply);
            return reply;
        }

        public Uri Uri(string offset, bool live, string? handle = null) =>
            new($"{api.Collections}/{DocumentApi.Collection}/shape?handle={handle ?? Handle}&offset={offset}" + (live ? "&live=true" : "") + Where(query), UriKind.Relative);

        public async Task<Reply> Get(string target, params (string Name, string Value)[] headers) =>
            await Get(new Uri(target, UriKind.Relative), headers);

        public async Task<Reply> Get(Uri target, params (string Name, string Value)[] headers)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            var token = TestContext.Current!.Execution.CancellationToken;
            using var response = await _http.SendAsync(request, token);
            var bytes = await response.Content.ReadAsByteArrayAsync(token);
            JsonElement json = default;
            if (bytes.Length > 0 && bytes[0] == '{')
            {
                using var document = JsonDocument.Parse(bytes);
                json = document.RootElement.Clone();
            }

            return new Reply(
                response.StatusCode, bytes, json,
                Header(response, "TB-Shape-Handle"), response.Headers.ETag?.ToString(), Header(response, "Cache-Control"),
                Header(response, "Vary"), Header(response, "TB-Shape-Next-Offset"), Header(response, "TB-Shape-Up-To-Date"),
                response.Content.Headers.ContentType?.MediaType, response.Headers.Location, target);
        }
        private void Apply(Reply reply)
        {
            if (reply.Status != HttpStatusCode.OK)
            {
                return;
            }

            using var document = JsonDocument.Parse(reply.Body);
            foreach (var change in document.RootElement.GetProperty("changes").EnumerateArray())
            {
                foreach (var op in change.GetProperty("ops").EnumerateArray())
                {
                    var id = op.GetProperty("id").GetString()!;
                    if (op.GetProperty("op").GetString() == "upsert")
                    {
                        State[id] = op.GetProperty("document").Clone();
                    }
                    else
                    {
                        State.Remove(id);
                    }
                }
            }

            NextOffset = reply.NextOffset ?? NextOffset;
            Handle = reply.Handle ?? Handle;
        }

        private static string Where(string? query) => query is null ? "" : "&where=" + System.Uri.EscapeDataString(query);

        private static string? Header(HttpResponseMessage response, string name) =>
            response.Headers.NonValidated.TryGetValues(name, out var values) ? values.SingleOrDefault()
            : response.Content.Headers.NonValidated.TryGetValues(name, out var content) ? content.SingleOrDefault() : null;
    }
}
