using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Usage;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.Fast.Management;

public sealed class UsageApiTests
{
    private static readonly Faker Faker = new();

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

    private static List<(string Start, int Reads, int Writes)> Points(JsonElement usage) =>
        [.. usage.GetProperty("series").EnumerateArray().Select(point => (
            point.GetProperty("start").GetDateTimeOffset().ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture),
            point.GetProperty("reads").GetInt32(), point.GetProperty("writes").GetInt32()))];

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
