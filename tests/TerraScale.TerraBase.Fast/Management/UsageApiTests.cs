using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Bogus;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraScale.TerraBase.Features.Management;
using TerraScale.TerraBase.Features.Management.Usage;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Management;

public sealed class UsageApiTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task Database_usage_counts_storage_documents_and_operations()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        using var fresh = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(database.Path + "/usage")));
        var puts = Faker.Random.Int(3, 6);
        var gets = Faker.Random.Int(1, 4);
        var lists = Faker.Random.Int(1, 3);
        await WriteAsync(data, database, "people", puts);
        (await data.DeleteAsync(Uri(Document(database, "people", "d0")))).EnsureSuccessStatusCode();
        await WriteAsync(data, database, "orders", 1);
        for (var i = 0; i < gets; i++)
        {
            (await data.GetAsync(Uri(Document(database, "people", "d1")))).EnsureSuccessStatusCode();
        }

        for (var i = 0; i < lists; i++)
        {
            (await data.GetAsync(Uri($"{database.Data}/collections/people/documents"))).EnsureSuccessStatusCode();
        }

        using var usage = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(database.Path + "/usage")));

        await Assert.That(fresh.RootElement.GetProperty("reads").GetInt64()).IsEqualTo(0);
        await Assert.That(fresh.RootElement.GetProperty("documents").GetInt64()).IsEqualTo(0);
        var root = usage.RootElement;
        await Assert.That(root.GetProperty("databaseId").GetString()).IsEqualTo(database.Id);
        await Assert.That(root.GetProperty("storageBytes").GetInt64()).IsGreaterThan(0);
        await Assert.That(root.GetProperty("collections").GetInt64()).IsEqualTo(2);
        await Assert.That(root.GetProperty("documents").GetInt64()).IsEqualTo(puts - 1 + 1);
        await Assert.That(root.GetProperty("reads").GetInt64()).IsEqualTo(gets + lists);
        await Assert.That(root.GetProperty("writes").GetInt64()).IsEqualTo(puts + 1 + 1);
    }

    [Test]
    public async Task Transactions_patches_and_queries_count_committed_writes_and_answered_reads()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var transactions = Uri($"{database.Data}/transactions");
        var (a, b) = (ManagementApi.DocumentId("a"), ManagementApi.DocumentId("b"));

        // Each write of a committed transaction counts; its assertions do not.
        using var committed = await data.PostAsJsonAsync(transactions, new
        {
            ops = new object[]
            {
                new { op = "create", collection = "people", id = a, document = new { name = Faker.Name.FullName() } },
                new { op = "create", collection = "people", id = b, document = new { name = Faker.Name.FullName() } },
                new { op = "assert", collection = "people", id = ManagementApi.DocumentId("c"), absent = true },
            },
        });
        // A failed assertion rolls the transaction back: its delete is not counted.
        using var rolledBack = await data.PostAsJsonAsync(transactions, new
        {
            ops = new object[]
            {
                new { op = "delete", collection = "people", id = a },
                new { op = "assert", collection = "people", id = b, absent = true },
            },
        });
        using var patch = new HttpRequestMessage(HttpMethod.Patch, Uri(ManagementApi.DocumentPath(database, "people", "a")))
        {
            Content = new StringContent("""{"city":"Lisbon"}""", System.Text.Encoding.UTF8, "application/merge-patch+json"),
        };
        using var patched = await data.SendAsync(patch);
        using var queried = await data.PostAsJsonAsync(Uri($"{database.Data}/collections/people/query"), new { limit = 1 });
        // Rejected before the engine answers: no read.
        using var unknownCollection = await data.GetAsync(Uri(ManagementApi.DocumentPath(database, "nothing", "a")));

        using var usage = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(database.Path + "/usage")));

        await Assert.That(committed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(rolledBack.StatusCode).IsEqualTo(HttpStatusCode.PreconditionFailed);
        await Assert.That(patched.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(queried.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(unknownCollection.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(usage.RootElement.GetProperty("writes").GetInt64()).IsEqualTo(3);
        await Assert.That(usage.RootElement.GetProperty("reads").GetInt64()).IsEqualTo(1);
        await Assert.That(usage.RootElement.GetProperty("documents").GetInt64()).IsEqualTo(2);
    }

    [Test]
    public async Task Project_usage_sums_every_database()
    {
        await using var api = await ManagementApi.StartAsync();
        var first = await api.CreateManagedDatabaseAsync("first");
        var second = await api.CreateManagedDatabaseAsync(first.Organization, first.Project, "second");
        using var data = await api.CreateClientAsync(first.Organization, "documents:read", "documents:write");
        await WriteAsync(data, first, "people", 2);
        await WriteAsync(data, second, "people", 3);
        (await data.GetAsync(Uri(Document(second, "people", "d0")))).EnsureSuccessStatusCode();

        using var usage = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(first.ProjectPath + "/usage")));

        var root = usage.RootElement;
        var items = root.GetProperty("items").EnumerateArray().ToList();
        await Assert.That(root.GetProperty("projectId").GetString()).IsEqualTo(first.Project);
        await Assert.That(root.GetProperty("databases").GetInt64()).IsEqualTo(2);
        await Assert.That(items.Select(item => item.GetProperty("databaseId").GetString()!).ToList()).IsEquivalentTo([first.Id, second.Id]);
        await Assert.That(items[0].GetProperty("databaseId").GetString()).IsEqualTo(first.Id);
        await Assert.That(root.GetProperty("documents").GetInt64()).IsEqualTo(5);
        await Assert.That(root.GetProperty("writes").GetInt64()).IsEqualTo(5);
        await Assert.That(root.GetProperty("reads").GetInt64()).IsEqualTo(1);
        await Assert.That(root.GetProperty("storageBytes").GetInt64()).IsEqualTo(items.Sum(item => item.GetProperty("storageBytes").GetInt64()));
    }

    [Test]
    public async Task Project_usage_reads_every_database_across_catalog_pages()
    {
        await using var api = await ManagementApi.StartAsync();
        var first = await api.CreateManagedDatabaseAsync("db-0");
        List<string> ids = [first.Id];
        // More than one catalog page of 10 databases.
        for (var i = 1; i < 12; i++)
        {
            ids.Add((await api.CreateManagedDatabaseAsync(first.Organization, first.Project, $"db-{i}")).Id);
        }

        using var usage = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(first.ProjectPath + "/usage")));

        await Assert.That(usage.RootElement.GetProperty("databases").GetInt64()).IsEqualTo(12);
        await Assert.That(usage.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("databaseId").GetString()!).ToList())
            .IsEquivalentTo(ids, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Project_usage_whose_counts_outlast_its_deadline_is_too_expensive()
    {
        var time = new FakeTimeProvider();
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(time: time, services: hook.Install);
        var first = await api.CreateManagedDatabaseAsync("db-0");
        await api.CreateManagedDatabaseAsync(first.Organization, first.Project, "db-1");
        // Each database's count takes 3 seconds of the request's 5.
        hook.BeforeBegin = (database, _) =>
        {
            if (database.ProjectId == first.Project)
            {
                time.Advance(TimeSpan.FromSeconds(3));
            }

            return ValueTask.CompletedTask;
        };

        using var two = await api.Client.GetAsync(Uri(first.ProjectPath + "/usage"));
        await api.CreateManagedDatabaseAsync(first.Organization, first.Project, "db-2");
        using var three = await api.Client.GetAsync(Uri(first.ProjectPath + "/usage"));

        // The third count would start 6 seconds in.
        await Assert.That(two.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(three.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await three.Content.ReadFromJsonAsync<string>()).IsEqualTo("query too expensive");
    }

    [Test]
    public async Task Project_usage_starts_no_count_after_a_tenant_lookup_that_outlasts_its_deadline()
    {
        var time = new FakeTimeProvider();
        var hook = new UsageCommitHook();
        var slowLookups = false;
        await using var api = await ManagementApi.StartAsync(time: time, services: services =>
        {
            hook.Install(services);
            var registration = services.Single(service => service.ServiceType == typeof(IManagementService));
            services.Remove(registration);
            services.AddSingleton(provider => SlowManagement.Wrap((IManagementService)registration.ImplementationFactory!(provider), method =>
            {
                // Resolving a database's tenant takes 6 seconds of the request's 5.
                if (Volatile.Read(ref slowLookups) && method == nameof(IManagementService.GetDatabaseTenantAsync))
                {
                    time.Advance(TimeSpan.FromSeconds(6));
                }
            }));
        });
        var database = await api.CreateManagedDatabaseAsync();
        var counts = 0;
        hook.BeforeBegin = (opened, _) =>
        {
            if (opened.DatabaseId == database.Id)
            {
                Interlocked.Increment(ref counts);
            }

            return ValueTask.CompletedTask;
        };
        Volatile.Write(ref slowLookups, true);

        using var response = await api.Client.GetAsync(Uri(database.ProjectPath + "/usage"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.Content.ReadFromJsonAsync<string>()).IsEqualTo("query too expensive");
        await Assert.That(counts).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Overlapping_flushes_count_interleaved_operations_once_and_retry_failed_commits(bool failFirstCommit)
    {
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(time: new FakeTimeProvider(), services: hook.Install);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        var meter = api.Server.Services.GetRequiredService<UsageMeter>();
        await WriteAsync(data, database, "people", 3);
        (await data.GetAsync(Uri(Document(database, "people", "d1")))).EnsureSuccessStatusCode();

        using var firstGate = hook.PauseCommit(failFirstCommit);
        var firstFlush = meter.FlushAsync(CancellationToken.None).AsTask();
        await firstGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WriteAsync(data, database, "people", 2);
        for (var i = 0; i < 2; i++)
        {
            (await data.GetAsync(Uri(Document(database, "people", "d1")))).EnsureSuccessStatusCode();
        }

        using var secondGate = hook.PauseCommit();
        var secondFlush = meter.FlushAsync(CancellationToken.None).AsTask();
        await Assert.That(secondFlush.IsCompleted).IsFalse();
        // These counts are uncommitted. A read-only connection must see none of this transaction's writes.
        await Assert.That(await ReadSavedOpsAsync(api.Server.DataDirectory, database.Id)).IsNull();
        firstGate.Dispose();
        if (failFirstCommit)
        {
            await Assert.ThrowsAsync<IOException>(() => firstFlush);
        }
        else
        {
            await firstFlush.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await secondGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (failFirstCommit)
        {
            await Assert.That(await ReadSavedOpsAsync(api.Server.DataDirectory, database.Id)).IsNull();
        }
        else
        {
            await AssertSavedAsync(api, database, reads: 1, writes: 3);
        }

        // More operations arrive after the second snapshot, and a usage reader overlaps its commit.
        await WriteAsync(data, database, "people", 1);
        (await data.GetAsync(Uri(Document(database, "people", "d1")))).EnsureSuccessStatusCode();
        var usageRead = api.Client.GetFromJsonAsync<JsonElement>(Uri(database.Path + "/usage"));
        secondGate.Dispose();
        await secondFlush.WaitAsync(TimeSpan.FromSeconds(10));
        var usage = await usageRead;
        await Assert.That(usage.GetProperty("reads").GetInt64()).IsEqualTo(4);
        await Assert.That(usage.GetProperty("writes").GetInt64()).IsEqualTo(6);
        await AssertSavedAsync(api, database, reads: 3, writes: 5);

        await meter.FlushAsync(CancellationToken.None);
        await AssertSavedAsync(api, database, reads: 4, writes: 6);
        await meter.FlushAsync(CancellationToken.None);
        await AssertSavedAsync(api, database, reads: 4, writes: 6);
        var final = await api.Client.GetFromJsonAsync<JsonElement>(Uri(database.Path + "/usage"));
        await Assert.That(final.GetProperty("reads").GetInt64()).IsEqualTo(4);
        await Assert.That(final.GetProperty("writes").GetInt64()).IsEqualTo(6);
    }

    [Test]
    public async Task A_flush_that_fails_after_its_commit_is_not_counted_again()
    {
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(time: new FakeTimeProvider(), services: hook.Install);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        var meter = api.Server.Services.GetRequiredService<UsageMeter>();
        await WriteAsync(data, database, "people", 2);

        // The usage write commits, then the flush fails as if disposing the transaction had thrown.
        using (var gate = hook.PauseAfterCommit(fail: true))
        {
            var flush = meter.FlushAsync(CancellationToken.None).AsTask();
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            gate.Dispose();
            await Assert.ThrowsAsync<IOException>(() => flush);
        }

        await AssertSavedAsync(api, database, reads: 0, writes: 2);
        var usage = await api.Client.GetFromJsonAsync<JsonElement>(Uri(database.Path + "/usage"));
        await WriteAsync(data, database, "people", 1);
        await meter.FlushAsync(CancellationToken.None);

        await Assert.That(usage.GetProperty("writes").GetInt64()).IsEqualTo(2);
        await AssertSavedAsync(api, database, reads: 0, writes: 3);
    }

    [Test]
    public async Task Counts_of_a_database_deleted_during_a_paused_flush_are_dropped_and_its_recreated_name_starts_over()
    {
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(time: new FakeTimeProvider(), services: hook.Install);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        var meter = api.Server.Services.GetRequiredService<UsageMeter>();
        await WriteAsync(data, database, "people", 1);
        await meter.FlushAsync(CancellationToken.None);
        await AssertSavedAsync(api, database, reads: 0, writes: 1);
        await WriteAsync(data, database, "people", 1);

        // The flush has its pending snapshot but has not acquired the catalog write transaction yet.
        using var gate = hook.PauseWrite();
        var flush = meter.FlushAsync(CancellationToken.None).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var deleted = await api.Client.DeleteAsync(Uri(database.Path));
        deleted.EnsureSuccessStatusCode();
        // The data API creates a database by name in the catalog: the same name is a new database with a new id.
        using var recreated = await data.PutAsync(Uri(database.Data), null);
        recreated.EnsureSuccessStatusCode();
        var recreatedId = (await recreated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await WriteAsync(data, database, "people", 1);
        (await data.GetAsync(Uri(Document(database, "people", "d0")))).EnsureSuccessStatusCode();
        gate.Dispose();
        await flush.WaitAsync(TimeSpan.FromSeconds(10));
        await meter.FlushAsync(CancellationToken.None);

        await Assert.That(recreatedId).IsNotEqualTo(database.Id);
        await Assert.That(await ReadSavedOpsAsync(api.Server.DataDirectory, database.Id)).IsNull();
        using var usage = await api.Client.GetAsync(Uri(database.Path + "/usage"));
        await Assert.That(usage.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await AssertSavedAsync(api, database with { Id = recreatedId }, reads: 1, writes: 1);
    }

    [Test]
    public async Task The_background_flush_saves_every_ten_seconds_and_retries_a_failed_usage_save_on_the_next_tick()
    {
        var time = new FakeTimeProvider();
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(time: time, services: hook.Install);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await WriteAsync(data, database, "people", 1);
        using var run = await api.Client.PostAsJsonAsync(Uri(database.Path + "/sql"), new { sql = "SELECT * FROM people WHERE city = 'x'" });
        run.EnsureSuccessStatusCode();

        // The first tick's usage save fails; the same tick still saves the query field counts.
        using var failure = hook.PauseCommit(fail: true);
        time.Advance(TimeSpan.FromSeconds(10));
        await failure.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        failure.Dispose();
        await UntilAsync(async () => await CountSavedQueryFieldsAsync(api.Server.DataDirectory, database.Id) == 1);
        await Assert.That(await ReadSavedOpsAsync(api.Server.DataDirectory, database.Id)).IsNull();

        // The next tick saves the counts the failure kept pending.
        time.Advance(TimeSpan.FromSeconds(10));
        await UntilAsync(async () => await ReadSavedOpsAsync(api.Server.DataDirectory, database.Id) is not null);
        await AssertSavedAsync(api, database, reads: 0, writes: 1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_database_deleted_while_its_usage_is_read_is_not_found_and_left_out_of_its_project(bool project)
    {
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(services: hook.Install);
        var deleted = await api.CreateManagedDatabaseAsync("deleted");
        var kept = await api.CreateManagedDatabaseAsync(deleted.Organization, deleted.Project, "kept");
        // The read has checked membership, measured the file and opened the storage; it pauses before its count transaction.
        UsageCommitHook.Gate? armed = new(fail: false);
        using var gate = armed;
        hook.BeforeBegin = async (database, cancellationToken) =>
        {
            if (database.DatabaseId == deleted.Id && Interlocked.Exchange(ref armed, null) is { } pending)
            {
                await pending.WaitAsync(cancellationToken);
            }
        };

        var read = api.Client.GetAsync(Uri((project ? deleted.ProjectPath : deleted.Path) + "/usage"));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (var removed = await api.Client.DeleteAsync(Uri(deleted.Path)))
        {
            removed.EnsureSuccessStatusCode();
        }

        gate.Dispose();
        using var response = await read.WaitAsync(TimeSpan.FromSeconds(10));

        if (project)
        {
            response.EnsureSuccessStatusCode();
            var usage = await response.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(usage.GetProperty("databases").GetInt64()).IsEqualTo(1);
            await Assert.That(usage.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("databaseId").GetString()!)).IsEquivalentTo([kept.Id]);
        }
        else
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
    }

    [Test]
    public async Task A_clean_shutdown_saves_pending_counts_across_a_restart()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-usage-restart-").FullName;
        try
        {
            var writes = Faker.Random.Int(1, 4);
            var reads = Faker.Random.Int(1, 4);
            string user, path;
            // The fake clocks never move, so the periodic flush never runs: only the shutdown flush can save these counts.
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory, time: new FakeTimeProvider()))
            {
                user = api.User;
                var database = await api.CreateManagedDatabaseAsync();
                path = database.Path + "/usage";
                using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
                await WriteAsync(data, database, "people", writes);
                for (var i = 0; i < reads; i++)
                {
                    (await data.GetAsync(Uri(Document(database, "people", "d0")))).EnsureSuccessStatusCode();
                }

                await Assert.That(await ReadSavedOpsAsync(directory, database.Id)).IsNull();
            }

            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory, time: new FakeTimeProvider()))
            {
                using var owner = api.As(user);
                var usage = await owner.GetFromJsonAsync<JsonElement>(Uri(path));
                await Assert.That(usage.GetProperty("reads").GetInt64()).IsEqualTo(reads);
                await Assert.That(usage.GetProperty("writes").GetInt64()).IsEqualTo(writes);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task AssertSavedAsync(ManagementApi api, ManagedDatabaseRef database, long reads, long writes)
    {
        var saved = await ReadSavedOpsAsync(api.Server.DataDirectory, database.Id);
        await Assert.That(saved).IsNotNull();
        using var ops = JsonDocument.Parse(saved!);
        await Assert.That(ops.RootElement.GetProperty("reads").GetInt64()).IsEqualTo(reads);
        await Assert.That(ops.RootElement.GetProperty("writes").GetInt64()).IsEqualTo(writes);
    }

    [Test]
    public async Task Usage_of_unknown_resources_is_not_found()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var organization = $"/v1/management/organizations/{Guid.NewGuid()}/projects/{database.Project}";

        using var unknownDatabase = await api.Client.GetAsync(Uri($"{database.ProjectPath}/databases/{Guid.NewGuid()}/usage"));
        using var unknownProjectDatabase = await api.Client.GetAsync(Uri($"/v1/management/organizations/{database.Organization}/projects/{Guid.NewGuid()}/databases/{database.Id}/usage"));
        using var unknownProject = await api.Client.GetAsync(Uri($"/v1/management/organizations/{database.Organization}/projects/{Guid.NewGuid()}/usage"));
        using var unknownOrganization = await api.Client.GetAsync(Uri(organization + "/usage"));

        await Assert.That(unknownDatabase.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknownProjectDatabase.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknownProject.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknownOrganization.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    private static async Task WriteAsync(HttpClient data, ManagedDatabaseRef database, string collection, int count)
    {
        await ManagementApi.CreateCollectionAsync(data, database, collection);
        for (var i = 0; i < count; i++)
        {
            (await data.PutAsJsonAsync(Uri(Document(database, collection, $"d{i}")), new { name = Faker.Name.FullName(), city = Faker.Address.City() })).EnsureSuccessStatusCode();
        }
    }

    // Reads the flushed counters straight from the catalog file, read-only.
    private static async Task<string?> ReadSavedOpsAsync(string dataDirectory, string databaseId)
    {
        var path = Path.Combine(dataDirectory, "_system.db");
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM documents WHERE collection = $collection AND id = 'ops';";
        command.Parameters.AddWithValue("$collection", $"databases/{databaseId}/usage");
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<long> CountSavedQueryFieldsAsync(string dataDirectory, string databaseId)
    {
        var path = Path.Combine(dataDirectory, "_system.db");
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM documents WHERE collection = $collection AND body IS NOT NULL;";
        command.Parameters.AddWithValue("$collection", $"databases/{databaseId}/query_fields");
        return (long)(await command.ExecuteScalarAsync())!;
    }

    // Background work finishes on its own schedule: polls for up to 10 seconds.
    private static async Task UntilAsync(Func<Task<bool>> done)
    {
        for (var attempt = 0; !await done(); attempt++)
        {
            if (attempt == 200)
            {
                throw new TimeoutException("the background flush did not save in time");
            }

            await Task.Delay(50);
        }
    }

    private static string Document(ManagedDatabaseRef database, string collection, string name) => ManagementApi.DocumentPath(database, collection, name);

    // Forwards every call to the management service, after telling the test which method runs.
    public class SlowManagement : DispatchProxy
    {
        private IManagementService _inner = null!;
        private Action<string> _before = null!;

        public static IManagementService Wrap(IManagementService inner, Action<string> before)
        {
            var proxy = Create<IManagementService, SlowManagement>();
            var slow = (SlowManagement)(object)proxy;
            slow._inner = inner;
            slow._before = before;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            _before(targetMethod.Name);
            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException error) when (error.InnerException is { } inner)
            {
                ExceptionDispatchInfo.Throw(inner);
                throw;
            }
        }
    }

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
