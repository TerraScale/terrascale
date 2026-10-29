using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Errors;
using TerraBase.Fast.Management;
using TerraBase.Fast.Storage;
using TerraBase.Features.Documents;
using TerraBase.Features.Memory;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class DocumentCacheTests
{
    [Test]
    public async Task Clock_eviction_gives_recently_read_documents_a_second_chance()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        const long Budget = 4 * 1024 * 1024;
        await using var management = await RequestLogMemoryFixture.StartAsync(settings =>
            settings["memory-budget"] = Budget.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var tenant = await CreateTenantAsync(management, "clock", enterprise: true, cancellationToken: deadline.Token);
        var padding = new string('x', 300 * 1024);
        foreach (var id in new[] { "a", "b", "c", "d", "e" })
        {
            using var written = await tenant.Data.PutAsJsonAsync(
                tenant.Database("clock", $"/collections/items/documents/by-key?id={StorageFixture.Id(id)}"),
                new { name = "first", padding }, deadline.Token);
            written.EnsureSuccessStatusCode();
        }
        foreach (var id in new[] { "a", "b", "c" })
        {
            await Assert.That((await GetAsync(tenant, "clock", "items", id, deadline.Token)).Name).IsEqualTo("first");
        }
        await StorageFixture.ExecuteAsync(await tenant.FileAsync(management.Server, "clock", deadline.Token),
            """UPDATE "items" SET doc = json_set(doc, '$.name', 'disk');""");
        await GetAsync(tenant, "clock", "items", "d", deadline.Token);
        await Assert.That((await GetAsync(tenant, "clock", "items", "b", deadline.Token)).Name).IsEqualTo("first");
        await GetAsync(tenant, "clock", "items", "e", deadline.Token);
        await Assert.That((await GetAsync(tenant, "clock", "items", "b", deadline.Token)).Name).IsEqualTo("first");
        await Assert.That((await GetAsync(tenant, "clock", "items", "c", deadline.Token)).Name).IsEqualTo("disk");
        await Assert.That((await GetAsync(tenant, "clock", "items", "a", deadline.Token)).Name).IsEqualTo("disk");
        await RequestLogMemoryFixture.DrainAsync(management.Server, deadline.Token);
        using var rest = management.Server.Services.GetRequiredService<IMemoryBudget>()
            .TryReserve(MemoryLane.Foreground, Budget - (Budget / 4));
        await Assert.That(rest).IsNotNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task An_old_snapshot_finishing_last_keeps_the_newer_cache_entry(bool page)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await using var management = await ManagementApi.StartAsync();
        using var tenant = await CreateTenantAsync(management, "race", cancellationToken: deadline.Token);
        await PutAsync(tenant, "race", "items", "hot", "old", deadline.Token);
        var database = await tenant.ContextAsync(management.Server, "race", deadline.Token);
        var store = (await management.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, deadline.Token))!;
        var cache = management.Server.Services.GetRequiredService<DocumentCache>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var query = new DocumentQuery("items", null, [], null, 50, 8 * 1024 * 1024);
        var old = ReadOldAsync();
        Exception? primaryFailure = null;
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            await PutAsync(tenant, "race", "items", "hot", "new", deadline.Token);
            await VerifyAsync("new");
            await StorageFixture.ExecuteAsync(await tenant.FileAsync(management.Server, "race", deadline.Token),
                """UPDATE "items" SET doc = '{"name":"disk"}';""");
            release.SetResult();
            await Assert.That(await old.WaitAsync(deadline.Token)).IsEqualTo("old");
            await VerifyAsync("new");
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            deadline.Cancel();
            release.TrySetResult();
            await StoragePoolTests.JoinCleanupAsync([old], primaryFailure);
        }

        async Task<string> ReadOldAsync()
        {
            var document = page
                ? (await cache.QueryAsync(database, store.Incarnation, query, null, async () =>
                {
                    await using var transaction = await store.BeginTransactionAsync(write: false, deadline.Token);
                    var rows = await transaction.QueryAsync(query, deadline.Token);
                    entered.SetResult();
                    await release.Task.WaitAsync(deadline.Token);
                    return new CachedQuery(rows, null);
                })).Page.Rows[0].Document
                : (await cache.GetAsync(new DocumentKey(database, "items", StorageFixture.Id("hot")), store.Incarnation, async () =>
                {
                    await using var transaction = await store.BeginTransactionAsync(write: false, deadline.Token);
                    var row = await transaction.GetAsync("items", StorageFixture.Id("hot"), deadline.Token);
                    entered.SetResult();
                    await release.Task.WaitAsync(deadline.Token);
                    return row;
                }))!;
            using var body = JsonDocument.Parse(document.Json);
            return body.RootElement.GetProperty("name").GetString()!;
        }

        async Task VerifyAsync(string name)
        {
            if (page)
            {
                await Assert.That(await ListAsync(tenant, "race", "items", deadline.Token))
                    .IsEquivalentTo([$"{StorageFixture.Id("hot")}:{name}"]);
            }
            else
            {
                await Assert.That((await GetAsync(tenant, "race", "items", "hot", deadline.Token)).Name).IsEqualTo(name);
            }
        }
    }

    [Test]
    public async Task Identical_document_ids_and_pages_stay_cached_within_their_tenant()
    {
        await using var management = await ManagementApi.StartAsync();
        using var first = await CreateTenantAsync(management, "shop");
        using var second = await CreateTenantAsync(management, "shop");
        var firstFile = await first.FileAsync(management.Server, "shop");
        var secondFile = await second.FileAsync(management.Server, "shop");
        var firstVersion = await PutAsync(first, "shop", "items", "same", "first");
        var secondVersion = await PutAsync(second, "shop", "items", "same", "second");
        await Assert.That(await GetAsync(first, "shop", "items", "same")).IsEqualTo((firstVersion, "first"));
        await Assert.That(await GetAsync(second, "shop", "items", "same")).IsEqualTo((secondVersion, "second"));
        await ListAsync(first, "shop", "items");
        await ListAsync(second, "shop", "items");
        await StorageFixture.ReplaceBodiesAsync(firstFile, "items", """{"name":"disk-first"}""");
        await StorageFixture.ReplaceBodiesAsync(secondFile, "items", """{"name":"disk-second"}""");

        await Assert.That(await GetAsync(first, "shop", "items", "same")).IsEqualTo((firstVersion, "first"));
        await Assert.That(await GetAsync(second, "shop", "items", "same")).IsEqualTo((secondVersion, "second"));
        await Assert.That(await ListAsync(first, "shop", "items")).IsEquivalentTo([$"{StorageFixture.Id("same")}:first"]);
        await Assert.That(await ListAsync(second, "shop", "items")).IsEquivalentTo([$"{StorageFixture.Id("same")}:second"]);
        await StorageFixture.ReplaceBodiesAsync(secondFile, "items", """{"name":"second"}""");
        var updated = await PutAsync(first, "shop", "items", "same", "updated");
        await Assert.That(await GetAsync(first, "shop", "items", "same")).IsEqualTo((updated, "updated"));
        await Assert.That(await ListAsync(first, "shop", "items")).IsEquivalentTo([$"{StorageFixture.Id("same")}:updated"]);
        await Assert.That(await GetAsync(second, "shop", "items", "same")).IsEqualTo((secondVersion, "second"));
        await Assert.That(await ListAsync(second, "shop", "items")).IsEquivalentTo([$"{StorageFixture.Id("same")}:second"]);
    }

    [Test]
    public async Task A_plan_change_keeps_old_store_writes_connected_to_current_cache_stamps()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await using var management = await ManagementApi.StartAsync();
        using var tenant = await CreateTenantAsync(management, "shop", cancellationToken: deadline.Token);
        await PutAsync(tenant, "shop", "items", "same", "first", deadline.Token);
        var database = await tenant.ContextAsync(management.Server, "shop", deadline.Token);
        await Assert.That(database.Plan).IsEqualTo(PlanTier.Hobby);
        var store = (await management.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, deadline.Token))!;
        using var service = management.Service(tenant.User);
        foreach (var (plan, tier) in new[] { ("pro", PlanTier.Pro), ("enterprise", PlanTier.Enterprise) })
        {
            using var changed = await service.PutAsJsonAsync(
                new Uri($"/v1/management/organizations/{tenant.OrganizationId}/plan", UriKind.Relative), new { plan }, deadline.Token);
            changed.EnsureSuccessStatusCode();
            await Assert.That((await changed.Content.ReadFromJsonAsync<JsonElement>(deadline.Token)).GetProperty("plan").GetString()).IsEqualTo(plan);
            await Assert.That(await tenant.ContextAsync(management.Server, "shop", deadline.Token)).IsEqualTo(database with { Plan = tier });
            await GetAsync(tenant, "shop", "items", "same", deadline.Token);
            await ListAsync(tenant, "shop", "items", deadline.Token);

            var transaction = await store.BeginTransactionAsync(write: true, deadline.Token);
            await using (transaction)
            {
                await transaction.PutAsync("items", StorageFixture.Id("same"), Encoding.UTF8.GetBytes($"{{\"name\":\"{plan}\"}}"), deadline.Token);
                await transaction.CommitAsync(deadline.Token);
            }

            await Assert.That((await GetAsync(tenant, "shop", "items", "same", deadline.Token)).Name).IsEqualTo(plan);
            await Assert.That(await ListAsync(tenant, "shop", "items", deadline.Token)).IsEquivalentTo([$"{StorageFixture.Id("same")}:" + plan]);
        }
    }

    [Test]
    public async Task Deleting_a_warmed_system_api_key_invalidates_authentication()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await using var management = await ManagementApi.StartAsync();
        using var tenant = await CreateTenantAsync(management, "shop", cancellationToken: deadline.Token);
        await PutAsync(tenant, "shop", "items", "a", "first", deadline.Token);
        await GetAsync(tenant, "shop", "items", "a", deadline.Token);
        var secret = tenant.Data.DefaultRequestHeaders.Authorization!.Parameter!;
        var keyId = await management.Server.Services.GetRequiredService<TerraBase.Features.ApiKeys.IApiKeyStore>().FindAsync(secret, deadline.Token) switch
        {
            TerraBase.Features.ApiKeys.ApiKey key => key.Id,
            NotFound => throw new InvalidOperationException("The test API key must exist."),
        };
        var system = await management.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(deadline.Token);
        var transaction = await system.BeginTransactionAsync(write: true, deadline.Token);
        await using (transaction)
        {
            await transaction.DeleteAsync("api_keys", keyId, deadline.Token);
            await transaction.CommitAsync(deadline.Token);
        }

        using var refused = await tenant.Data.GetAsync(tenant.Database("shop", $"/collections/items/documents/by-key?id={StorageFixture.Id("a")}"), deadline.Token);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Warm_cache_reads_observe_an_already_cancelled_token(bool list)
    {
        await using var management = await ManagementApi.StartAsync(services: services =>
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider()));
        var server = management.Server;
        using var tenant = await CreateTenantAsync(management, "cancelled");
        var client = tenant.Data;
        var initial = await PutAsync(tenant, "cancelled", "items", "a", "first");
        await GetAsync(tenant, "cancelled", "items", "a");
        await ListAsync(tenant, "cancelled", "items");
        var database = await tenant.ContextAsync(management.Server, "cancelled");
        var documents = server.Services.GetRequiredService<IDocumentService>();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        if (list)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => documents.QueryAsync(
                database, new DocumentQuery("items", null, [], null, 50, 8 * 1024 * 1024), null, cancelled.Token).AsTask());
        }
        else
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => documents.GetAsync(
                new DocumentKey(database, "items", StorageFixture.Id("a")), cancelled.Token).AsTask());
        }

        await Assert.That(await GetAsync(tenant, "cancelled", "items", "a")).IsEqualTo((initial, "first"));
        await Assert.That(await ListAsync(tenant, "cancelled", "items")).IsEquivalentTo([$"{StorageFixture.Id("a")}:first"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Reads_finishing_after_cache_disposal_return_data_without_reserving_memory(bool list)
    {
        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        const long Budget = 4 * 1024 * 1024;
        var completed = new CompletedCacheRead();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var management = await RequestLogMemoryFixture.StartAsync(settings =>
        {
            settings["memory-budget"] = Budget.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }, services => services.AddSingleton<IStartupFilter>(completed), clock);
        var server = management.Server;
        using var tenant = await CreateTenantAsync(management, "closing", cancellationToken: operationTimeout.Token);
        var client = tenant.Data;
        await PutAsync(tenant, "closing", "items", "a", "first", operationTimeout.Token);
        var database = await tenant.ContextAsync(management.Server, "closing", operationTimeout.Token);
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(database, operationTimeout.Token))!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var observing = false;
        var session = await StorageFixture.RentReaderAsync(engine, store);
        try
        {
            session.Connection.CreateFunction<string, string>("paused_body", body =>
            {
                if (!observing)
                {
                    return body;
                }

                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(20), operationTimeout.Token))
                {
                    throw new TimeoutException("cache-disposal test did not release the storage read");
                }

                return body;
            }, isDeterministic: true);
            observing = true;
        }
        finally
        {
            engine.Pool.Return(store, session);
        }

        await StorageFixture.ObserveBodiesAsync(store, "paused_body");

        var path = $"/v1/projects/{tenant.ProjectId}/databases/closing/collections/items/documents" + (list ? "" : $"/by-key?id={StorageFixture.Id("a")}");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Observe-Cache-Completion", "true");
        var pending = client.SendAsync(request, operationTimeout.Token);
        HttpResponseMessage? response = null;
        Exception? primaryFailure = null;
        try
        {
            await entered.Task.WaitAsync(operationTimeout.Token);
            var cache = server.Services.GetRequiredService<DocumentCache>();
            cache.Dispose();
            cache.Dispose();
            release.Set();
            response = await pending.WaitAsync(operationTimeout.Token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(operationTimeout.Token));
            var document = list ? json.RootElement.GetProperty("items")[0] : json.RootElement;
            await Assert.That(document.GetProperty("document").GetProperty("name").GetString()).IsEqualTo("first");
            await completed.Completion.Task.WaitAsync(operationTimeout.Token);
            await RequestLogMemoryFixture.DrainAsync(server, operationTimeout.Token);
            var budget = server.Services.GetRequiredService<IMemoryBudget>();
            using var recoveredBudget = budget.TryReserve(MemoryLane.Foreground, Budget);
            await Assert.That(recoveredBudget).IsNotNull();
            recoveredBudget!.Dispose();
            using var subsequent = await client.GetAsync(new Uri(path, UriKind.Relative), operationTimeout.Token);
            subsequent.EnsureSuccessStatusCode();
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            operationTimeout.Cancel();
            release.Set();
            response?.Dispose();
            await StoragePoolTests.JoinCleanupAsync([pending], primaryFailure);
        }
    }

    [Test]
    public async Task Point_reads_are_cached_until_a_write_or_delete()
    {
        await using var management = await ManagementApi.StartAsync();
        var server = management.Server;
        using var tenant = await CreateTenantAsync(management, "shop");
        var client = tenant.Data;
        var file = await tenant.FileAsync(server, "shop");
        var initial = await PutAsync(tenant, "shop", "people", "p1", "first");
        var other = await PutAsync(tenant, "shop", "people", "p2", "first");

        var miss = await GetAsync(tenant, "shop", "people", "p1");
        await StorageFixture.ReplaceBodiesAsync(file, "people", """{"name":"disk"}""");
        var hit = await GetAsync(tenant, "shop", "people", "p1");
        var neverRead = await GetAsync(tenant, "shop", "people", "p2");
        var written = await PutAsync(tenant, "shop", "people", "p1", "second");
        var afterWrite = await GetAsync(tenant, "shop", "people", "p1");
        await StorageFixture.ReplaceBodiesAsync(file, "people", """{"name":"disk"}""");
        var cachedAgain = await GetAsync(tenant, "shop", "people", "p1");
        using var deleted = await client.DeleteAsync(new Uri($"/v1/projects/{tenant.ProjectId}/databases/shop/collections/people/documents/by-key?id={StorageFixture.Id("p1")}", UriKind.Relative));
        using var afterDelete = await client.GetAsync(new Uri($"/v1/projects/{tenant.ProjectId}/databases/shop/collections/people/documents/by-key?id={StorageFixture.Id("p1")}", UriKind.Relative));

        await Assert.That(miss).IsEqualTo((initial, "first"));
        await Assert.That(hit).IsEqualTo((initial, "first"));
        await Assert.That(neverRead.Name).IsEqualTo("disk");
        await Assert.That(neverRead.Version).IsNotEqualTo(other);
        await Assert.That(written).IsNotEqualTo(initial);
        await Assert.That(afterWrite).IsEqualTo((written, "second"));
        await Assert.That(cachedAgain).IsEqualTo((written, "second"));
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(afterDelete.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task A_hot_set_of_small_documents_larger_than_4096_entries_stays_cached()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await using var management = await ManagementApi.StartAsync(settings =>
            settings["memory-budget"] = (256L * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var tenant = await CreateTenantAsync(management, "hot", enterprise: true, cancellationToken: deadline.Token);
        var database = await tenant.ContextAsync(management.Server, "hot", deadline.Token);
        var store = (await management.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, deadline.Token))!;
        var cache = management.Server.Services.GetRequiredService<DocumentCache>();
        var keys = Enumerable.Range(0, 6000).Select(key => new DocumentKey(database, "small", StorageFixture.Id($"hot{key}"))).ToList();
        var reads = 0;
        async Task ReadAllAsync()
        {
            foreach (var key in keys)
            {
                _ = await cache.GetAsync(key, store.Incarnation, () =>
                {
                    reads++;
                    return ValueTask.FromResult<StoredDocument?>(new StoredDocument(key.Id, 1, Encoding.UTF8.GetBytes("""{"name":"first"}"""), Guid.Empty));
                });
            }
        }

        await ReadAllAsync();
        await Assert.That(reads).IsEqualTo(keys.Count);
        await ReadAllAsync();
        await Assert.That(reads).IsEqualTo(keys.Count);
    }

    [Test]
    public async Task A_faulted_copy_refuses_a_document_it_has_cached()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await using var management = await ManagementApi.StartAsync();
        using var tenant = await CreateTenantAsync(management, "faulted", cancellationToken: deadline.Token);
        await PutAsync(tenant, "faulted", "items", "hot", "first", deadline.Token);
        var database = await tenant.ContextAsync(management.Server, "faulted", deadline.Token);
        var documents = management.Server.Services.GetRequiredService<IDocumentService>();
        var key = new DocumentKey(database, "items", StorageFixture.Id("hot"));
        await Assert.That(await documents.GetAsync(key, deadline.Token) is StoredDocument).IsTrue();
        await Assert.That(await documents.GetAsync(key, deadline.Token) is StoredDocument).IsTrue();

        var store = (await management.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, deadline.Token))!;
        await management.Server.Services.GetRequiredService<TursoStorageEngine>().Ledger.RecordAsync(database, store.Incarnation, "a test fault");
        await Assert.ThrowsAsync<StorageCopyFaultedException>(async () => await documents.GetAsync(key, deadline.Token));
    }

    [Test]
    public async Task List_pages_are_cached_until_the_collection_changes()
    {
        await using var management = await ManagementApi.StartAsync();
        var server = management.Server;
        using var tenant = await CreateTenantAsync(management, "shop");
        var client = tenant.Data;
        var file = await tenant.FileAsync(server, "shop");
        await PutAsync(tenant, "shop", "items", "a", "first");
        await PutAsync(tenant, "shop", "items", "b", "first");

        var miss = await ListAsync(tenant, "shop", "items");
        await StorageFixture.ReplaceBodiesAsync(file, "items", """{"name":"disk"}""");
        var hit = await ListAsync(tenant, "shop", "items");
        await PutAsync(tenant, "shop", "items", "c", "first");
        var afterWrite = await ListAsync(tenant, "shop", "items");
        using var deleted = await client.DeleteAsync(new Uri($"/v1/projects/{tenant.ProjectId}/databases/shop/collections/items/documents/by-key?id={StorageFixture.Id("b")}", UriKind.Relative));
        deleted.EnsureSuccessStatusCode();
        var afterDelete = await ListAsync(tenant, "shop", "items");

        await Assert.That(miss).IsEquivalentTo([$"{StorageFixture.Id("a")}:first", $"{StorageFixture.Id("b")}:first"]);
        await Assert.That(hit).IsEquivalentTo([$"{StorageFixture.Id("a")}:first", $"{StorageFixture.Id("b")}:first"]);
        await Assert.That(afterWrite).IsEquivalentTo([$"{StorageFixture.Id("a")}:disk", $"{StorageFixture.Id("b")}:disk", $"{StorageFixture.Id("c")}:first"]);
        await Assert.That(afterDelete).IsEquivalentTo([$"{StorageFixture.Id("a")}:disk", $"{StorageFixture.Id("c")}:first"]);
    }

    [Test]
    public async Task Writes_that_bypass_the_document_api_invalidate_the_cache()
    {
        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await using var management = await ManagementApi.StartAsync(services: services =>
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider()));
        var server = management.Server;
        using var tenant = await CreateTenantAsync(management, "shop", cancellationToken: operationTimeout.Token);
        var client = tenant.Data;
        var initial = await PutAsync(tenant, "shop", "people", "p1", "first", operationTimeout.Token);
        await GetAsync(tenant, "shop", "people", "p1", operationTimeout.Token);
        await ListAsync(tenant, "shop", "people", operationTimeout.Token);
        var engine = server.Services.GetRequiredService<IStorageEngine>();
        var shop = await tenant.ContextAsync(management.Server, "shop", operationTimeout.Token);
        var store = (await engine.OpenDatabaseAsync(shop, operationTimeout.Token))!;
        var transaction = await store.BeginTransactionAsync(write: true, operationTimeout.Token);
        long committed;
        await using (transaction)
        {
            committed = await transaction.PutAsync("people", StorageFixture.Id("p1"), Encoding.UTF8.GetBytes("""{"name":"applied"}"""), operationTimeout.Token);
            await transaction.CommitAsync(operationTimeout.Token);
        }

        var applied = await GetAsync(tenant, "shop", "people", "p1", operationTimeout.Token);
        var appliedPage = await ListAsync(tenant, "shop", "people", operationTimeout.Token);
        await engine.DeleteDatabaseAsync(shop, operationTimeout.Token);
        await Assert.That(await engine.CreateDatabaseAsync(shop, operationTimeout.Token)).IsTrue();
        using var recreated = await client.GetAsync(new Uri($"/v1/projects/{tenant.ProjectId}/databases/shop/collections/people/documents/by-key?id={StorageFixture.Id("p1")}", UriKind.Relative), operationTimeout.Token);
        await StorageFixture.CreateCollectionAsync(tenant, "shop", "people", operationTimeout.Token);
        var recreatedPage = await ListAsync(tenant, "shop", "people", operationTimeout.Token);

        await Assert.That(applied.Version).IsNotEqualTo(initial);
        await Assert.That(applied).IsEqualTo((ETags.Version(transaction.Scope, "people", StorageFixture.Id("p1"), committed, Encoding.UTF8.GetBytes("""{"name":"applied"}""")), "applied"));
        await Assert.That(appliedPage).IsEquivalentTo([$"{StorageFixture.Id("p1")}:applied"]);
        await Assert.That(recreated.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(recreatedPage).IsEmpty();
    }

    [Test]
    public async Task Repeated_writes_invalidate_each_touched_stamp_once_at_commit()
    {
        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        const int Writes = 1024;
        await using var management = await ManagementApi.StartAsync();
        var server = management.Server;
        using var tenant = await CreateTenantAsync(management, "shop", cancellationToken: operationTimeout.Token);
        var initial = await PutAsync(tenant, "shop", "items", "hot", "first", operationTimeout.Token);
        var shop = await tenant.ContextAsync(management.Server, "shop", operationTimeout.Token);
        var store = (await server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(shop, operationTimeout.Token))!;
        var stamps = server.Services.GetRequiredService<IWriteStamps>();
        var transaction = await store.BeginTransactionAsync(write: true, operationTimeout.Token);
        long committed = 0;
        await using (transaction)
        {
            var json = Encoding.UTF8.GetBytes("""{"name":"applied"}""");
            for (var i = 0; i < Writes; i++)
            {
                committed = await transaction.PutAsync("items", StorageFixture.Id("hot"), json, operationTimeout.Token);
            }
            await Assert.That(await GetAsync(tenant, "shop", "items", "hot", operationTimeout.Token)).IsEqualTo((initial, "first"));
            await ListAsync(tenant, "shop", "items", operationTimeout.Token);
            var before = stamps.Collection(shop, "items");
            await transaction.CommitAsync(operationTimeout.Token);
            await Assert.That(stamps.Collection(shop, "items") - before).IsEqualTo(1);
        }

        await Assert.That(await GetAsync(tenant, "shop", "items", "hot", operationTimeout.Token)).IsEqualTo((ETags.Version(transaction.Scope, "items", StorageFixture.Id("hot"), committed, Encoding.UTF8.GetBytes("""{"name":"applied"}""")), "applied"));
        await Assert.That(await ListAsync(tenant, "shop", "items", operationTimeout.Token)).IsEquivalentTo([$"{StorageFixture.Id("hot")}:applied"]);
    }

    [Test]
    public async Task Writes_started_together_in_one_engine_transaction_all_invalidate()
    {
        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        const int Documents = 32;
        await using var management = await ManagementApi.StartAsync();
        var server = management.Server;
        using var tenant = await CreateTenantAsync(management, "shop", enterprise: true, cancellationToken: operationTimeout.Token);
        var ids = Enumerable.Range(0, Documents).Select(i => $"d{i}").ToList();
        foreach (var id in ids)
        {
            await PutAsync(tenant, "shop", "items", id, "first", operationTimeout.Token);
            await GetAsync(tenant, "shop", "items", id, operationTimeout.Token);
        }

        var shop = await tenant.ContextAsync(management.Server, "shop", operationTimeout.Token);
        var store = (await server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(shop, operationTimeout.Token))!;
        var transaction = await store.BeginTransactionAsync(write: true, operationTimeout.Token);
        await using (transaction)
        {
            var pending = new List<Task>();
            Exception? primaryFailure = null;
            try
            {
                foreach (var id in ids)
                {
                    pending.Add(transaction.PutAsync("items", StorageFixture.Id(id), Encoding.UTF8.GetBytes("""{"name":"applied"}"""), operationTimeout.Token).AsTask());
                }

                foreach (var id in ids)
                {
                    pending.Add(GetAsync(tenant, "shop", "items", id, operationTimeout.Token));
                }

                await Task.WhenAll(pending).WaitAsync(operationTimeout.Token);
                await transaction.CommitAsync(operationTimeout.Token);
                var verification = ids.Select(async id => (await GetAsync(tenant, "shop", "items", id, operationTimeout.Token)).Name).ToList();
                pending.AddRange(verification);
                var names = await Task.WhenAll(verification).WaitAsync(operationTimeout.Token);
                await Assert.That(names).All().Satisfy(name => name.IsEqualTo("applied"));
            }
            catch (Exception error)
            {
                primaryFailure = error;
                throw;
            }
            finally
            {
                operationTimeout.Cancel();
                await StoragePoolTests.JoinCleanupAsync(pending, primaryFailure);
            }
        }
    }

    [Test]
    public async Task The_cache_stays_within_its_share_of_the_memory_budget()
    {
        const int Documents = 48;
        const int DocumentBytes = 64 * 1024;
        const long Budget = 4 * 1024 * 1024;
        await using var management = await RequestLogMemoryFixture.StartAsync(settings =>
        {
            settings["memory-budget"] = Budget.ToString(System.Globalization.CultureInfo.InvariantCulture);
        });
        var server = management.Server;
        using var tenant = await CreateTenantAsync(management, "big");
        using (var service = management.Service(tenant.User))
        using (var changed = await service.PutAsJsonAsync(
            new Uri($"/v1/management/organizations/{tenant.OrganizationId}/plan", UriKind.Relative), new { plan = "enterprise" }))
        {
            changed.EnsureSuccessStatusCode();
        }

        var client = tenant.Data;
        var padding = new string('x', DocumentBytes);
        for (var i = 0; i < Documents; i++)
        {
            using var written = await client.PutAsJsonAsync(
                new Uri($"/v1/projects/{tenant.ProjectId}/databases/big/collections/blobs/documents/by-key?id={StorageFixture.Id($"b{i}")}", UriKind.Relative),
                new { name = "first", padding });
            written.EnsureSuccessStatusCode();
        }

        for (var i = 0; i < Documents; i++)
        {
            await GetAsync(tenant, "big", "blobs", $"b{i}");
        }
        await RequestLogMemoryFixture.DrainAsync(server);
        var budget = server.Services.GetRequiredService<IMemoryBudget>();
        using (var everything = budget.TryReserve(MemoryLane.Foreground, Budget))
        {
            await Assert.That(everything).IsNull();
        }

        using var recovered = budget.TryReserve(MemoryLane.Foreground, Budget - (Budget / 4));
        await Assert.That(recovered).IsNotNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Oversized_reads_bypass_admission_and_preserve_useful_cached_entries(bool list)
    {
        const long Budget = 4 * 1024 * 1024;
        const int CacheBytes = (int)(Budget / 4);
        ObservedMemoryBudget? observed = null;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var management = await RequestLogMemoryFixture.StartAsync(settings =>
        {
            settings["memory-budget"] = Budget.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }, services =>
        {
            services.AddSingleton<DocumentCache>(provider => new DocumentCache(
                provider.GetRequiredService<TerraBaseOptions>(),
                observed = new ObservedMemoryBudget(provider.GetRequiredService<IMemoryBudget>(), CacheBytes),
                provider.GetRequiredService<IWriteStamps>(), provider.GetRequiredService<IMeterFactory>()));
        }, time: clock);
        var server = management.Server;
        using var tenant = await CreateTenantAsync(management, "oversized");
        using (var service = management.Service(tenant.User))
        using (var changed = await service.PutAsJsonAsync(
            new Uri($"/v1/management/organizations/{tenant.OrganizationId}/plan", UriKind.Relative), new { plan = "enterprise" }))
        {
            changed.EnsureSuccessStatusCode();
        }

        var client = tenant.Data;
        var file = await tenant.FileAsync(server, "oversized");
        using (var response = await client.PutAsJsonAsync(
            new Uri($"/v1/projects/{tenant.ProjectId}/databases/oversized/collections/small/documents/by-key?id={StorageFixture.Id("a")}", UriKind.Relative),
            new { name = "first", padding = new string('x', 896 * 1024) }))
        {
            response.EnsureSuccessStatusCode();
        }

        string[] ids = list ? ["a", "b"] : ["a"];
        var padding = new string('x', list ? CacheBytes / 2 - 64 : CacheBytes - 64);
        foreach (var id in ids)
        {
            using var response = await client.PutAsJsonAsync(
                new Uri($"/v1/projects/{tenant.ProjectId}/databases/oversized/collections/large/documents/by-key?id={StorageFixture.Id(id)}", UriKind.Relative),
                new { name = "first", padding });
            response.EnsureSuccessStatusCode();
        }
        await RequestLogMemoryFixture.DrainAsync(server);
        async Task<List<string>> ReadAsync(string collection)
        {
            await RequestLogMemoryFixture.WaitForRequestsAsync(server, TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            return list
                ? await ListAsync(tenant, "oversized", collection)
                : [$"{StorageFixture.Id("a")}:{(await GetAsync(tenant, "oversized", collection, "a")).Name}"];
        }

        await Assert.That(await ReadAsync("small")).IsEquivalentTo([$"{StorageFixture.Id("a")}:first"]);
        await StorageFixture.ExecuteAsync(file,
            """UPDATE "small" SET doc = json_set(doc, '$.name', 'disk');""");
        var budget = observed ?? throw new InvalidOperationException("cache admission observation was not registered");
        for (var i = 0; i < 5; i++)
        {
            await StorageFixture.ExecuteAsync(file,
                $"UPDATE \"large\" SET doc = json_set(doc, '$.name', 'disk{i}');");
            await Assert.That(await ReadAsync("large")).IsEquivalentTo(ids.Select(id => $"{StorageFixture.Id(id)}:disk{i}"));
            await Assert.That(budget.OversizedAttempts).IsEqualTo(0);
            await Assert.That(await ReadAsync("small")).IsEquivalentTo([$"{StorageFixture.Id("a")}:first"]);
        }
    }

    [Test]
    public async Task Concurrent_reads_never_return_a_version_older_than_an_acknowledged_write()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        const int Writes = 100;
        await using var management = await ManagementApi.StartAsync();
        var server = management.Server;
        using var tenant = await CreateTenantAsync(management, "race", enterprise: true, cancellationToken: deadline.Token);
        var client = tenant.Data;
        var written = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        written["1"] = await PutAsync(tenant, "race", "items", "hot", "1", deadline.Token);
        long acknowledged = 1;
        var database = await tenant.ContextAsync(management.Server, "race", deadline.Token);
        var store = (await server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, deadline.Token))!;
        var transaction = await store.BeginTransactionAsync(write: false, deadline.Token);
        await using var transactionCleanup = transaction;
        var observed = new System.Collections.Concurrent.ConcurrentQueue<(string Name, string Version)>();
        var stale = 0;
        var reads = 0;

        async Task ReadAsync(bool list, CancellationToken cancellationToken)
        {
            var floor = Volatile.Read(ref acknowledged);
            var document = list
                ? await ListedDocumentAsync(tenant, cancellationToken)
                : await GetAsync(tenant, "race", "items", "hot", cancellationToken);
            var version = long.Parse(document.Name, System.Globalization.CultureInfo.InvariantCulture);
            observed.Enqueue((document.Name, document.Version));
            Interlocked.Increment(ref reads);
            if (version < floor)
            {
                Interlocked.Increment(ref stale);
            }
        }

        var readers = new List<Task>();
        Exception? primaryFailure = null;
        try
        {
            for (var i = 1; i < Writes; i++)
            {
                var name = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                readers = [.. Enumerable.Range(0, 6).Select(reader => ReadAsync(list: reader % 3 == 0, deadline.Token))];
                written[name] = await PutAsync(tenant, "race", "items", "hot", name, deadline.Token);
                Volatile.Write(ref acknowledged, i + 1);
                await Task.WhenAll(readers).WaitAsync(deadline.Token);
            }

            readers = [.. Enumerable.Range(0, 6).Select(reader => ReadAsync(list: reader % 3 == 0, deadline.Token))];
            await Task.WhenAll(readers).WaitAsync(deadline.Token);
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            deadline.Cancel();
            await StoragePoolTests.JoinCleanupAsync(readers, primaryFailure);
        }

        await Assert.That(acknowledged).IsEqualTo(Writes);
        await Assert.That(reads).IsEqualTo(6 * Writes);
        await Assert.That(stale).IsEqualTo(0);
        await Assert.That(observed.Where(read => written[read.Name] != read.Version).ToList()).IsEmpty();
    }

    private static async Task<(string Version, string Name)> ListedDocumentAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        var client = tenant.Data;
        using var page = JsonDocument.Parse(await client.GetStringAsync(new Uri($"/v1/projects/{tenant.ProjectId}/databases/race/collections/items/documents", UriKind.Relative), cancellationToken));
        var document = page.RootElement.GetProperty("items")[0];
        return (document.GetProperty("version").GetString()!, document.GetProperty("document").GetProperty("name").GetString()!);
    }

    private static async Task<Tenant> CreateTenantAsync(ManagementApi management, string database, bool enterprise = false, CancellationToken cancellationToken = default)
    {
        var tenant = await management.CreateTenantAsync("documents:read", "documents:write");
        try
        {
            if (enterprise)
            {
                using var service = management.Service(tenant.User);
                using var changed = await service.PutAsJsonAsync(
                    new Uri($"/v1/management/organizations/{tenant.OrganizationId}/plan", UriKind.Relative), new { plan = "enterprise" }, cancellationToken);
                changed.EnsureSuccessStatusCode();
            }

            await tenant.ContextAsync(management.Server, database, cancellationToken);
            foreach (var collection in new[] { "items", "people", "blobs", "small", "large" })
            {
                await StorageFixture.CreateCollectionAsync(tenant, database, collection, cancellationToken);
            }

            return tenant;
        }
        catch
        {
            tenant.Dispose();
            throw;
        }
    }

    private static async Task<string> PutAsync(
        Tenant tenant, string database, string collection, string id, string name, CancellationToken cancellationToken = default)
    {
        var client = tenant.Data;
        using var response = await client.PutAsJsonAsync(
            new Uri($"/v1/projects/{tenant.ProjectId}/databases/{database}/collections/{collection}/documents/by-key?id={StorageFixture.Id(id)}", UriKind.Relative), new { name }, cancellationToken);
        return (await response.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("version").GetString()!;
    }

    private static async Task<(string Version, string Name)> GetAsync(
        Tenant tenant, string database, string collection, string id, CancellationToken cancellationToken = default)
    {
        var client = tenant.Data;
        using var document = JsonDocument.Parse(await client.GetStringAsync(
            new Uri($"/v1/projects/{tenant.ProjectId}/databases/{database}/collections/{collection}/documents/by-key?id={StorageFixture.Id(id)}", UriKind.Relative), cancellationToken));
        return (document.RootElement.GetProperty("version").GetString()!, document.RootElement.GetProperty("document").GetProperty("name").GetString()!);
    }

    private static async Task<List<string>> ListAsync(
        Tenant tenant, string database, string collection, CancellationToken cancellationToken = default)
    {
        var client = tenant.Data;
        using var response = await client.GetAsync(
            new Uri($"/v1/projects/{tenant.ProjectId}/databases/{database}/collections/{collection}/documents", UriKind.Relative), cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK)
            .Because($"GET {response.RequestMessage?.RequestUri?.PathAndQuery}: HTTP {(int)response.StatusCode}: {text[..Math.Min(text.Length, 4096)]}");
        using var page = JsonDocument.Parse(text);
        return [.. page.RootElement.GetProperty("items").EnumerateArray().Select(document =>
            $"{document.GetProperty("id").GetString()}:{document.GetProperty("document").GetProperty("name").GetString()}")];
    }

    private sealed class CompletedCacheRead : IStartupFilter
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                if (context.Request.Headers.ContainsKey("X-Observe-Cache-Completion"))
                {
                    context.Response.OnCompleted(() =>
                    {
                        Completion.TrySetResult();
                        return Task.CompletedTask;
                    });
                }

                await following(context);
            });
            next(app);
        };
    }

    private sealed class ObservedMemoryBudget(IMemoryBudget inner, long cacheLimit) : IMemoryBudget
    {
        private int _oversizedAttempts;

        public int OversizedAttempts => Volatile.Read(ref _oversizedAttempts);

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes)
        {
            if (bytes > cacheLimit)
            {
                Interlocked.Increment(ref _oversizedAttempts);
            }

            return inner.TryReserve(lane, bytes);
        }

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes, TenantContext? tenant)
        {
            if (bytes > cacheLimit)
            {
                Interlocked.Increment(ref _oversizedAttempts);
            }

            return inner.TryReserve(lane, bytes, tenant);
        }
    }
}
