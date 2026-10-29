using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Fast.Documents;
using TerraBase.Features.Memory;
using TerraBase.Features.Shapes;
using TerraBase.Primitives;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.Fast.Shapes;

public sealed class ClientAccessRetainedShapeTests
{
    [Test]
    public async Task Owner_authorization_accepts_deep_retained_documents_in_snapshot_pages_and_closed_chunks()
    {
        await using var api = await CollectionAccessRulesTests.StartAsync();
        var rules = CollectionAccessRulesTests.OwnerRules();
        await CollectionAccessRulesTests.DeclareAsync(api, rules);
        var actor = DocumentApi.NewId();
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { owner = actor })).Status.ShouldBe(HttpStatusCode.Created);
        var deep = "{\"owner\":\"" + actor + "\",\"deep\":"
            + string.Concat(Enumerable.Repeat("{\"a\":", 80)) + "1" + new string('}', 80) + "}";
        await using (var connection = new SqliteConnection(DocumentApi.ConnectionString(await api.DatabaseFileAsync())))
        {
            await connection.OpenAsync();

            await StoredStates.WriteAsync(connection, DocumentApi.Collection, id, deep);
        }
        using var reader = CollectionAccessRulesTests.Reader(api, actor);
        var shape = Shape(api);
        using var bootstrap = await reader.GetAsync(Relative(shape + "?offset=-1"));
        bootstrap.StatusCode.ShouldBe(HttpStatusCode.TemporaryRedirect);
        using var page = await reader.GetAsync(bootstrap.Headers.Location);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var pageBody = JsonDocument.Parse(await page.Content.ReadAsByteArrayAsync(),
            new JsonDocumentOptions { MaxDepth = DocumentJson.MaxStoredDepth + 5 });
        await AssertDeepAsync(pageBody.RootElement, id, actor);

        var handle = pageBody.RootElement.GetProperty("handle").GetString()!;
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        await using (var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            var collection = (await transaction.GetCollectionAsync(DocumentApi.Collection, CancellationToken.None))!;
            var definition = new ShapeSealDefinition(pageBody.RootElement.GetProperty("shape_id").GetString()!, handle,
                collection.Name, collection.Id, RandomNumberGenerator.GetBytes(16), store.Incarnation,
                OwnerAuthorization: new DocumentOwnerProof(collection.Id, rules.Owner!, actor, rules.Fingerprint()));
            _ = await api.Server.Services.GetRequiredService<IShapeSealer>().SealUpToHeadAsync(store, definition, 0, CancellationToken.None);
        }
        using (var retained = await api.Server.Services.GetRequiredService<IShapeSealer>().ReadChunkAsync(store, handle, 0, CancellationToken.None))
        {
            await Assert.That(retained).IsNotNull();
            using var retainedBody = JsonDocument.Parse(retained!.Body,
                new JsonDocumentOptions { MaxDepth = DocumentJson.MaxStoredDepth + 5 });
            await AssertDeepAsync(retainedBody.RootElement, id, actor);
        }
        using var chunk = await reader.GetAsync(Relative($"{shape}?handle={handle}&offset=0"));
        chunk.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var chunkBody = JsonDocument.Parse(await chunk.Content.ReadAsByteArrayAsync(),
            new JsonDocumentOptions { MaxDepth = DocumentJson.MaxStoredDepth + 5 });
        await Assert.That(chunkBody.RootElement.GetProperty("up_to_date").GetBoolean()).IsFalse();
        await AssertDeepAsync(chunkBody.RootElement, id, actor);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_retained_snapshot_cannot_deliver_transferred_payloads_even_with_a_cached_etag(bool warm)
    {
        await using var api = await CollectionAccessRulesTests.StartAsync();
        var rules = CollectionAccessRulesTests.OwnerRules();
        await CollectionAccessRulesTests.DeclareAsync(api, rules);
        var alice = DocumentApi.NewId();
        var bob = DocumentApi.NewId();
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { owner = alice, secret = "sealed-owner-payload" })).Status.ShouldBe(HttpStatusCode.Created);
        using var reader = CollectionAccessRulesTests.Reader(api, alice);
        var shape = Shape(api);
        using var bootstrap = await reader.GetAsync(Relative(shape + "?offset=-1"));
        bootstrap.StatusCode.ShouldBe(HttpStatusCode.TemporaryRedirect);
        var handle = bootstrap.Headers.GetValues("TB-Shape-Handle").Single();
        string? etag = null;
        if (warm)
        {
            using var page = await reader.GetAsync(bootstrap.Headers.Location);
            page.StatusCode.ShouldBe(HttpStatusCode.OK);
            etag = page.Headers.ETag!.ToString();
        }
        (await api.PutAsync(id, new { owner = bob, secret = "new-owner-payload" })).Status.ShouldBe(HttpStatusCode.OK);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, bootstrap.Headers.Location);
            request.Headers.TryAddWithoutValidation("If-None-Match", etag ?? "*");
            using var refused = await reader.SendAsync(request);
            await OpaqueAsync(refused, handle, id, "sealed-owner-payload", "new-owner-payload");
        }
        var fresh = await SnapshotAsync(reader, shape);
        await Assert.That(fresh.Ids).IsEmpty();
        using var recipient = CollectionAccessRulesTests.Reader(api, bob);
        await Assert.That((await SnapshotAsync(recipient, shape)).Ids).IsEquivalentTo([id]);
    }

    [Test]
    public async Task Retained_closed_and_live_chunks_refuse_transferred_payloads_and_recover_with_a_fresh_snapshot()
    {
        await using var api = await CollectionAccessRulesTests.StartAsync();
        var rules = CollectionAccessRulesTests.OwnerRules();
        await CollectionAccessRulesTests.DeclareAsync(api, rules);
        var alice = DocumentApi.NewId();
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { owner = alice, secret = "initial" })).Status.ShouldBe(HttpStatusCode.Created);
        using var reader = CollectionAccessRulesTests.Reader(api, alice);
        var shape = Shape(api);
        var initial = await SnapshotAsync(reader, shape);
        (await api.PutAsync(id, new { owner = alice, secret = "sealed-chunk-payload" })).Status.ShouldBe(HttpStatusCode.OK);
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        await using (var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            var collection = (await transaction.GetCollectionAsync(DocumentApi.Collection, CancellationToken.None))!;
            var definition = new ShapeSealDefinition(initial.ShapeId, initial.Handle, collection.Name, collection.Id,
                RandomNumberGenerator.GetBytes(16), store.Incarnation, OwnerAuthorization: new DocumentOwnerProof(collection.Id, rules.Owner!, alice, rules.Fingerprint()));
            _ = await api.Server.Services.GetRequiredService<IShapeSealer>().SealUpToHeadAsync(store, definition,
                long.Parse(initial.Offset, CultureInfo.InvariantCulture), CancellationToken.None);
        }
        using (var chunk = await api.Server.Services.GetRequiredService<IShapeSealer>().ReadChunkAsync(store, initial.Handle,
            long.Parse(initial.Offset, CultureInfo.InvariantCulture), CancellationToken.None))
        {
            await Assert.That(chunk).IsNotNull();
            using var body = JsonDocument.Parse(chunk!.Body);
            await Assert.That(Ids(body.RootElement)).IsEquivalentTo([id]);
        }
        (await api.PutAsync(id, new { owner = DocumentApi.NewId(), secret = "transferred" })).Status.ShouldBe(HttpStatusCode.OK);
        foreach (var suffix in new[] { "", "&live=true" })
        {
            using var refused = await reader.GetAsync(Relative($"{shape}?handle={initial.Handle}&offset={initial.Offset}{suffix}"));
            await OpaqueAsync(refused, initial.Handle, id, "sealed-chunk-payload");
        }
        await Assert.That((await SnapshotAsync(reader, shape)).Ids).IsEmpty();
    }

    [Test]
    public async Task Retained_continuations_recheck_both_cursor_ids_even_when_the_user_filter_emits_no_payload()
    {
        await using var api = await CollectionAccessRulesTests.StartAsync();
        var rules = CollectionAccessRulesTests.OwnerRules();
        await CollectionAccessRulesTests.DeclareAsync(api, rules);
        var actor = DocumentApi.NewId();
        using var reader = CollectionAccessRulesTests.Reader(api, actor);
        var where = """{"eq":["/status","open"]}""";
        if (ShapeFilter.Parse(where) is not ShapeFilter filter)
        {
            throw new InvalidOperationException("invalid fixture filter");
        }
        var shape = Shape(api) + "?where=" + Uri.EscapeDataString(where);
        var initial = await SnapshotAsync(reader, shape);
        var ids = Enumerable.Range(0, 3).Select(_ => DocumentApi.NewId()).Order(StringComparer.Ordinal).ToArray();
        for (var i = 0; i < ids.Length; i++)
        {
            (await api.PutAsync(ids[i], new { owner = actor, status = i == 0 ? "closed" : "open" })).Status.ShouldBe(HttpStatusCode.Created);
        }
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        var transactions = api.Server.Services.GetRequiredService<IShapeSnapshotTransactions>();
        long offset;
        await using (var transaction = await transactions.BeginAsync(store, write: false, CancellationToken.None))
        {
            var collection = (await transaction.GetCollectionAsync(DocumentApi.Collection, CancellationToken.None))!;
            var time = new FakeTimeProvider();
            var definition = new ShapeSnapshotDefinition(initial.Handle, initial.ShapeId, collection.Name, collection.Id,
                RandomNumberGenerator.GetBytes(16), store.Incarnation, document =>
                {
                    time.Advance(ShapeSnapshots.PageDeadline);
                    return filter.Matches(document);
                }, rules.OwnerFilter(actor));
            var snapshots = (IShapeSnapshotStorage)transaction;
            string cursor;
            (offset, cursor) = await snapshots.ShapeSnapshotHeadAsync(definition.ResumeScope, time.GetUtcNow(), CancellationToken.None);
            string? after = null;
            for (var page = 0; page < 3; page++)
            {
                using var body = await snapshots.BuildShapeSnapshotPageAsync(definition, offset, after, cursor,
                    api.Server.Services.GetRequiredService<IMemoryBudget>(), time, CancellationToken.None);
                await Assert.That(body.NextId).IsEqualTo(page < 2 ? ids[page] : null);
                await using var writer = await transactions.BeginAsync(store, write: true, CancellationToken.None);
                await ((IShapeSnapshotStorage)writer).InsertShapeSnapshotPageAsync(definition, offset, page, body, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), CancellationToken.None);
                _ = await writer.CommitAsync(CancellationToken.None);
                after = body.NextId;
            }
        }
        var first = $"{shape}&handle={initial.Handle}&offset={ShapeSnapshots.PageOffset(offset, null)}";
        using (var permitted = await reader.GetAsync(Relative(first)))
        {
            permitted.StatusCode.ShouldBe(HttpStatusCode.OK);
            await Assert.That(Ids(await permitted.Content.ReadFromJsonAsync<JsonElement>())).IsEmpty();
        }
        (await api.PutAsync(ids[0], new { owner = DocumentApi.NewId(), status = "closed" })).Status.ShouldBe(HttpStatusCode.OK);
        foreach (var after in new string?[] { null, ids[0] })
        {
            using var refused = await reader.GetAsync(Relative($"{shape}&handle={initial.Handle}&offset={ShapeSnapshots.PageOffset(offset, after)}"));
            await OpaqueAsync(refused, initial.Handle, ids[0], ids[1]);
        }
        (await api.PutAsync(ids[2], new { owner = DocumentApi.NewId(), status = "open" })).Status.ShouldBe(HttpStatusCode.OK);
        using (var refused = await reader.GetAsync(Relative($"{shape}&handle={initial.Handle}&offset={ShapeSnapshots.PageOffset(offset, ids[1])}")))
        {
            await OpaqueAsync(refused, initial.Handle, ids[1], ids[2]);
        }
        await Assert.That((await SnapshotAsync(reader, shape)).Ids).IsEquivalentTo([ids[1]]);
    }

    [Test]
    public async Task Every_shared_flight_waiter_rechecks_ownership_after_the_payload_read()
    {
        var paused = new PausedFlight();
        await using var api = await DocumentApi.StartAsync(services: services =>
        {
            var original = services.Last(descriptor => descriptor.ServiceType == typeof(IShapeFlights<ShapeRead>));
            services.AddSingleton<IShapeFlights<ShapeRead>>(provider =>
            {
                paused.Inner = (IShapeFlights<ShapeRead>)original.ImplementationFactory!(provider);
                return paused;
            });
        });
        using (var owner = api.Management.As(api.Tenant.User))
        {
            await api.Management.VerifyProjectBindingAsync(api.Tenant.OrganizationId, api.Tenant.ProjectId, owner);
        }
        await CollectionAccessRulesTests.DeclareAsync(api, CollectionAccessRulesTests.OwnerRules());
        var actor = DocumentApi.NewId();
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { owner = actor, secret = "flight-owner-payload" })).Status.ShouldBe(HttpStatusCode.Created);
        using var reader = CollectionAccessRulesTests.Reader(api, actor);
        using var bootstrap = await reader.GetAsync(Relative(Shape(api) + "?offset=-1"));
        bootstrap.StatusCode.ShouldBe(HttpStatusCode.TemporaryRedirect);
        var handle = bootstrap.Headers.GetValues("TB-Shape-Handle").Single();
        paused.Enabled = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var first = reader.GetAsync(bootstrap.Headers.Location, timeout.Token);
        var second = reader.GetAsync(bootstrap.Headers.Location, timeout.Token);
        try
        {
            await paused.Ready.Task.WaitAsync(timeout.Token);
            (await api.PutAsync(id, new { owner = DocumentApi.NewId(), secret = "transferred" })).Status.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            paused.Release.TrySetResult();
        }
        using var firstResponse = await first;
        using var secondResponse = await second;
        await OpaqueAsync(firstResponse, handle, id, "flight-owner-payload");
        await OpaqueAsync(secondResponse, handle, id, "flight-owner-payload");
    }

    private sealed class PausedFlight : IShapeFlights<ShapeRead>
    {
        public IShapeFlights<ShapeRead> Inner { get; set; } = null!;
        public bool Enabled { get; set; }
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _waiters;

        public long NextCursor(long? input) => Inner.NextCursor(input);

        public async ValueTask<ShapeFlightResult<ShapeRead>> ReadAsync(ShapeFlightRequest request, ShapeHeadProbe probe,
            ShapeTailReader<ShapeRead> read, CancellationToken cancellationToken)
        {
            var result = await Inner.ReadAsync(request, probe, read, cancellationToken);
            if (Enabled)
            {
                if (Interlocked.Increment(ref _waiters) == 2)
                {
                    Ready.TrySetResult();
                }
                try
                {
                    await Release.Task.WaitAsync(cancellationToken);
                }
                catch
                {
                    if (result is ShapeFlight<ShapeRead> flight)
                    {
                        flight.Dispose();
                    }
                    throw;
                }
            }
            return result;
        }
    }

    private static async Task AssertDeepAsync(JsonElement body, string id, string actor)
    {
        var operation = body.GetProperty("changes").EnumerateArray()
            .SelectMany(change => change.GetProperty("ops").EnumerateArray()).Single();
        await Assert.That(operation.GetProperty("op").GetString()).IsEqualTo("upsert");
        await Assert.That(operation.GetProperty("id").GetString()).IsEqualTo(id);
        var document = operation.GetProperty("document");
        await Assert.That(document.GetProperty("owner").GetString()).IsEqualTo(actor);
        var nested = document.GetProperty("deep");
        for (var level = 0; level < 80; level++)
        {
            nested = nested.GetProperty("a");
        }
        await Assert.That(nested.GetInt32()).IsEqualTo(1);
    }

    private static async Task OpaqueAsync(HttpResponseMessage response, params string[] sensitive)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        foreach (var header in new[] { "ETag", "TB-Shape-Handle", "TB-Shape-Next-Offset", "TB-Shape-Cursor", "Location" })
        {
            await Assert.That(response.Headers.Contains(header)).IsFalse();
        }
        var body = await response.Content.ReadAsStringAsync();
        foreach (var value in sensitive)
        {
            await Assert.That(body.Contains(value, StringComparison.Ordinal)).IsFalse();
        }
    }

    private static async Task<(string Handle, string ShapeId, string Offset, string[] Ids)> SnapshotAsync(HttpClient reader, string shape)
    {
        using var bootstrap = await reader.GetAsync(Relative(Append(shape, "offset=-1")));
        bootstrap.StatusCode.ShouldBe(HttpStatusCode.TemporaryRedirect);
        using var page = await reader.GetAsync(bootstrap.Headers.Location);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await page.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("handle").GetString()!, body.GetProperty("shape_id").GetString()!,
            body.GetProperty("next_offset").GetString()!, Ids(body));
    }

    private static string[] Ids(JsonElement body) => [.. body.GetProperty("changes").EnumerateArray()
        .SelectMany(change => change.GetProperty("ops").EnumerateArray()).Select(op => op.GetProperty("id").GetString()!)];
    private static string Shape(DocumentApi api) => $"{api.Collections}/{DocumentApi.Collection}/shape";
    private static string Append(string shape, string query) => shape + (shape.Contains('?', StringComparison.Ordinal) ? "&" : "?") + query;
    private static Uri Relative(string target) => new(target, UriKind.Relative);
}
