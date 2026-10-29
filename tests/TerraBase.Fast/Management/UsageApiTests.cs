using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Usage;
using TerraBase.Primitives;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.Fast.Management;

public sealed class UsageApiTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task Database_usage_counts_storage_documents_and_operations()
    {

        await using var api = await ManagementApi.StartAsync(services: services =>
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider()));
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
    public async Task Reads_and_writes_are_counted_apart_per_hour_and_day_and_summed_by_project()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 6, 10, 30, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(time: time);
        var first = await api.CreateManagedDatabaseAsync("first");
        var second = await api.CreateManagedDatabaseAsync(first.Organization, first.Project, "second");
        using var data = await api.CreateClientAsync(first.Organization, "documents:read", "documents:write");
        var meter = api.Server.Services.GetRequiredService<UsageMeter>();

        var (writes, reads) = (Faker.Random.Int(2, 5), Faker.Random.Int(1, 4));
        await WriteAsync(data, first, "people", writes);
        for (var i = 0; i < reads; i++)
        {
            (await data.GetAsync(Uri(Document(first, "people", "d0")))).EnsureSuccessStatusCode();
        }

        await meter.FlushAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromHours(1));
        var (laterWrites, laterReads) = (Faker.Random.Int(1, 3), Faker.Random.Int(1, 3));
        await WriteAsync(data, first, "people", laterWrites);
        for (var i = 0; i < laterReads; i++)
        {
            (await data.GetAsync(Uri($"{first.Data}/collections/people/documents"))).EnsureSuccessStatusCode();
        }

        (await api.Client.PostAsJsonAsync(Uri(first.Path + "/sql"), new { sql = "SELECT * FROM people" })).EnsureSuccessStatusCode();
        await WriteAsync(data, second, "people", 1);
        laterReads += 1;

        var hours = $"?granularity=hour&from=2026-10-06T10:00:00Z&to=2026-10-06T12:00:00Z";
        var database = await api.Client.GetFromJsonAsync<JsonElement>(Uri(first.Path + "/usage" + hours));
        var day = await api.Client.GetFromJsonAsync<JsonElement>(Uri(first.Path + "/usage?granularity=day&from=2026-10-06T00:00:00Z&to=2026-10-07T00:00:00Z"));
        var recent = await api.Client.GetFromJsonAsync<JsonElement>(Uri(first.Path + "/usage"));
        var project = await api.Client.GetFromJsonAsync<JsonElement>(Uri(first.ProjectPath + "/usage" + hours));
        var collections = await api.Client.GetFromJsonAsync<JsonElement>(Uri(first.Path + "/collections"));

        await Assert.That(Points(database)).IsEquivalentTo(
            [("2026-10-06T10:00:00+00:00", reads, writes), ("2026-10-06T11:00:00+00:00", laterReads, laterWrites)], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await AssertPeriodAsync(database, "2026-10-06T10:00:00+00:00", "2026-10-06T12:00:00+00:00", "hour", reads + laterReads, writes + laterWrites);
        await Assert.That(database.GetProperty("reads").GetInt64()).IsEqualTo(reads + laterReads);
        await Assert.That(database.GetProperty("writes").GetInt64()).IsEqualTo(writes + laterWrites);
        await Assert.That(database.GetProperty("logicalBytes").GetInt64())
            .IsEqualTo(collections.GetProperty("items").EnumerateArray().Sum(item => item.GetProperty("bytes").GetInt64()));
        await Assert.That(database.GetProperty("logicalBytes").GetInt64()).IsGreaterThan(0);
        await Assert.That(Points(day)).IsEquivalentTo([("2026-10-06T00:00:00+00:00", reads + laterReads, writes + laterWrites)]);

        var last = Points(recent);
        await Assert.That(last.Count).IsEqualTo(24);
        await Assert.That(last[^2..]).IsEquivalentTo(Points(database), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(last[..^2].All(point => point is (_, 0, 0))).IsTrue();

        await AssertPeriodAsync(project, "2026-10-06T10:00:00+00:00", "2026-10-06T12:00:00+00:00", "hour", reads + laterReads, writes + laterWrites + 1);
        await Assert.That(Points(project)).IsEquivalentTo(
            [("2026-10-06T10:00:00+00:00", reads, writes), ("2026-10-06T11:00:00+00:00", laterReads, laterWrites + 1)], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(project.GetProperty("databases").GetInt64()).IsEqualTo(2);
        await Assert.That(project.GetProperty("items").EnumerateArray().All(item => item.GetProperty("series").GetArrayLength() == 0)).IsTrue();
        await Assert.That(project.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("databaseId").GetString() == second.Id)
            .GetProperty("period").GetProperty("writes").GetInt64()).IsEqualTo(1);

        await meter.FlushAsync(CancellationToken.None);
        var saved = await api.Client.GetFromJsonAsync<JsonElement>(Uri(first.Path + "/usage" + hours));
        await Assert.That(Points(saved)).IsEquivalentTo(Points(database), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(await BucketsAsync(api.Server.DataDirectory, first.Id, "hours")).IsEquivalentTo(["2026-10-06T10", "2026-10-06T11"]);
    }

    [Test]
    public async Task A_flush_prunes_expired_buckets_and_saves_held_counts_past_retention_only_in_kept_days_and_lifetime()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-usage-retention-").FullName;
        try
        {
            ManagedDatabaseRef database;
            string user;
            void Settings(Dictionary<string, string> settings)
            {
                settings["data-directory"] = directory;
            }

            await using (var api = await ManagementApi.StartAsync(Settings, time: new FakeTimeProvider(Instant("2026-10-06T10:30:00Z"))))
            {
                user = api.User;
                database = await api.CreateManagedDatabaseAsync();
                using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
                await WriteAsync(data, database, "people", 1);
                await api.Server.Services.GetRequiredService<UsageMeter>().FlushAsync(CancellationToken.None);
                await Assert.That(await BucketsAsync(directory, database.Id, "hours")).IsEquivalentTo(["2026-10-06T10"]);
            }

            await using (var api = await ManagementApi.StartAsync(Settings, time: new FakeTimeProvider(Instant("2026-11-11T11:30:00Z"))))
            {
                using var member = api.As(user);
                var hour = await member.GetFromJsonAsync<JsonElement>(Uri(database.Path + "/usage?from=2026-10-06T10:00:00Z&to=2026-10-06T11:00:00Z"));
                var day = await member.GetFromJsonAsync<JsonElement>(Uri(database.Path + "/usage?granularity=day&from=2026-10-06T00:00:00Z&to=2026-10-07T00:00:00Z"));
                await Assert.That(await BucketsAsync(directory, database.Id, "hours")).IsEquivalentTo(["2026-10-06T10"]);
                await Assert.That(hour.GetProperty("period").GetProperty("writes").GetInt64()).IsEqualTo(0);
                await Assert.That(day.GetProperty("period").GetProperty("writes").GetInt64()).IsEqualTo(1);

                using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
                await WriteAsync(data, database, "people", 1);
                var meter = api.Server.Services.GetRequiredService<UsageMeter>();
                var owner = new DatabaseOwner(database.Organization, database.Project, database.Id);

                foreach (var held in new[] { "2026-10-01T05:00:00Z", "2026-10-07T12:00:00Z", "2026-10-07T11:00:00Z", "2025-10-08T05:00:00Z", "2025-10-07T05:00:00Z" })
                {
                    meter.Add(new UsageKey(owner, Instant(held).ToUnixTimeSeconds() / 3600), new OpCounts(1, 0));
                }

                await meter.FlushAsync(CancellationToken.None);

                await Assert.That(await BucketsAsync(directory, database.Id, "hours")).IsEquivalentTo(["2026-10-07T12", "2026-11-11T11"]);
                await Assert.That(await BucketsAsync(directory, database.Id, "days"))
                    .IsEquivalentTo(["2025-10-08", "2026-10-01", "2026-10-06", "2026-10-07", "2026-11-11"]);
                await AssertSavedAsync(api, database, reads: 5, writes: 2);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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

        using var committed = await data.PostAsJsonAsync(transactions, new
        {
            ops = new object[]
            {
                new { op = "create", collection = "people", id = a, document = new { name = Faker.Name.FullName() } },
                new { op = "create", collection = "people", id = b, document = new { name = Faker.Name.FullName() } },
                new { op = "assert", collection = "people", id = ManagementApi.DocumentId("c"), absent = true },
            },
        });

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

        var time = new FakeTimeProvider();
        await using var api = await ManagementApi.StartAsync(time: time);
        var first = await api.CreateManagedDatabaseAsync("first");
        time.Advance(TimeSpan.FromMilliseconds(1));
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

        await Assert.That(root.GetProperty("collections").GetInt64()).IsEqualTo(2);
        await Assert.That(root.GetProperty("collections").GetInt64()).IsEqualTo(items.Sum(item => item.GetProperty("collections").GetInt64()));
        await Assert.That(root.GetProperty("writes").GetInt64()).IsEqualTo(5);
        await Assert.That(root.GetProperty("reads").GetInt64()).IsEqualTo(1);
        await Assert.That(root.GetProperty("storageBytes").GetInt64()).IsEqualTo(items.Sum(item => item.GetProperty("storageBytes").GetInt64()));
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
        var organization = $"/v1/management/organizations/{EntityIds.New()}/projects/{database.Project}";

        using var unknownDatabase = await api.Client.GetAsync(Uri($"{database.ProjectPath}/databases/{EntityIds.New()}/usage"));
        using var unknownProjectDatabase = await api.Client.GetAsync(Uri($"/v1/management/organizations/{database.Organization}/projects/{EntityIds.New()}/databases/{database.Id}/usage"));
        using var unknownProject = await api.Client.GetAsync(Uri($"/v1/management/organizations/{database.Organization}/projects/{EntityIds.New()}/usage"));
        using var unknownOrganization = await api.Client.GetAsync(Uri(organization + "/usage"));

        await Assert.That(unknownDatabase.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknownProjectDatabase.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknownProject.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknownOrganization.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    private static List<(string Start, int Reads, int Writes)> Points(JsonElement usage) =>
        [.. usage.GetProperty("series").EnumerateArray().Select(point => (
            point.GetProperty("start").GetDateTimeOffset().ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture),
            point.GetProperty("reads").GetInt32(), point.GetProperty("writes").GetInt32()))];

    private static DateTimeOffset Instant(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    private static async Task AssertPeriodAsync(JsonElement usage, string from, string to, string granularity, long reads, long writes)
    {
        var period = usage.GetProperty("period");
        await Assert.That(period.GetProperty("from").GetDateTimeOffset()).IsEqualTo(DateTimeOffset.Parse(from, System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(period.GetProperty("to").GetDateTimeOffset()).IsEqualTo(DateTimeOffset.Parse(to, System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(period.GetProperty("granularity").GetString()).IsEqualTo(granularity);
        await Assert.That(period.GetProperty("reads").GetInt64()).IsEqualTo(reads);
        await Assert.That(period.GetProperty("writes").GetInt64()).IsEqualTo(writes);
    }

    private static async Task<List<string>> BucketsAsync(string dataDirectory, string databaseId, string granularity)
    {
        var path = Path.Combine(dataDirectory, "_system.db");
        await using var connection = new SqliteConnection(TestMasterKey.ConnectionString(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM documents WHERE collection = $collection AND body IS NOT NULL ORDER BY id;";
        command.Parameters.AddWithValue("$collection", $"databases/{databaseId}/usage-{granularity}");
        List<string> ids = [];
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0).Split('~')[0]);
        }

        return ids;
    }

    private static async Task WriteAsync(HttpClient data, ManagedDatabaseRef database, string collection, int count)
    {
        await ManagementApi.CreateCollectionAsync(data, database, collection);
        for (var i = 0; i < count; i++)
        {
            (await data.PutAsJsonAsync(Uri(Document(database, collection, $"d{i}")), new { name = Faker.Name.FullName(), city = Faker.Address.City() })).EnsureSuccessStatusCode();
        }
    }

    private static async Task<string?> ReadSavedOpsAsync(string dataDirectory, string databaseId)
    {
        var path = Path.Combine(dataDirectory, "_system.db");
        await using var connection = new SqliteConnection(TestMasterKey.ConnectionString(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM documents WHERE collection = $collection AND id LIKE 'ops~%';";
        command.Parameters.AddWithValue("$collection", $"databases/{databaseId}/usage");
        return await command.ExecuteScalarAsync() as string;
    }

    private static string Document(ManagedDatabaseRef database, string collection, string name) => ManagementApi.DocumentPath(database, collection, name);

    public class SlowManagement : DispatchProxy
    {
        private IManagementService _inner = null!;
        private Action<string> _before = null!;
        private Func<string, object?>? _answer;

        internal static IManagementService Wrap(IManagementService inner, Action<string> before, Func<string, object?>? answer = null)
        {
            var proxy = Create<IManagementService, SlowManagement>();
            var slow = (SlowManagement)(object)proxy;
            slow._inner = inner;
            slow._before = before;
            slow._answer = answer;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            _before(targetMethod.Name);
            if (_answer?.Invoke(targetMethod.Name) is { } answer)
            {
                return answer;
            }

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
