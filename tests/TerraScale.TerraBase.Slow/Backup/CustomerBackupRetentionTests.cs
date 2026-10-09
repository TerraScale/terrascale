using System.Globalization;
using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.Slow.Backup;

public sealed class CustomerBackupRetentionTests
{
    [Test]
    public async Task Retention_drains_more_than_ten_thousand_expired_snapshots_without_deleting_a_later_snapshots_wal()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => ManualShipping(oci, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var database = await CreateManagedDatabaseAsync(api, cancellationToken);
        using var data = api.DataClient(await api.Server.IssueAsync(database.Organization, ["documents:read", "documents:write"], cancellationToken));
        await SetAsync(api, database, "customer", cancellationToken);
        await CreateCollectionAsync(data, database, "people", cancellationToken);
        var faker = new Faker();
        var before = faker.Lorem.Sentence(12);
        var after = faker.Lorem.Sentence(12);
        await PutAsync(data, database, "before", before, cancellationToken);
        await ShipAsync(api, time, cancellationToken);
        var currentSnapshot = oci.CustomerObjects.Keys.Single(key => key.StartsWith(File(database) + "/snapshots/", StringComparison.Ordinal));
        var generation = currentSnapshot.Split('/')[^1].Split('-')[1];
        await PutAsync(data, database, "after", after, cancellationToken);
        await ShipAsync(api, time, cancellationToken);
        var neededSegment = oci.CustomerObjects.Keys.Single(key => key.StartsWith(File(database) + "/wal/", StringComparison.Ordinal));
        const int expiredCount = 10_002;
        var first = time.GetUtcNow().AddDays(-3).ToUnixTimeMilliseconds();
        for (var index = 0; index < expiredCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The retained real snapshot has position zero, below every candidate's position. Its WAL remains needed.
            var key = string.Create(CultureInfo.InvariantCulture,
                $"{File(database)}/snapshots/{first + index:D13}-{generation}-0000000000009999");
            oci.SeedObject(customer: true, key, []);
        }

        var snapshotListings = 0;
        oci.CustomerListingOverride = (prefix, keys) =>
        {
            if (prefix == File(database) + "/snapshots/")
            {
                Interlocked.Increment(ref snapshotListings);
            }

            return keys;
        };
        var previous = expiredCount + 1;
        for (var pass = 0; pass < 2; pass++)
        {
            time.Advance(TimeSpan.FromHours(1));
            await ShipAsync(api, time, cancellationToken);
            await Assert.That(Volatile.Read(ref snapshotListings)).IsGreaterThan(10);
            var remaining = oci.CustomerObjects.Keys.Count(key => key.StartsWith(File(database) + "/snapshots/", StringComparison.Ordinal));
            await Assert.That(remaining).IsLessThan(previous);
            previous = remaining;
            await Assert.That(oci.CustomerObjects.ContainsKey(currentSnapshot)).IsTrue();
            await Assert.That(oci.CustomerObjects.ContainsKey(neededSegment)).IsTrue();
            var restored = await RestoreAsync(api, database, await LatestAsync(api.Client, database, "customer", cancellationToken), "backlog-" + pass, "customer", cancellationToken);
            await AssertDocumentAsync(data, restored, "before", before, cancellationToken);
            await AssertDocumentAsync(data, restored, "after", after, cancellationToken);
        }

        // One snapshot before the cutoff remains as the floor; all other expired snapshots have drained.
        await Assert.That(previous).IsEqualTo(2);
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    private static void ManualShipping(FakeOci oci, IServiceCollection services)
    {
        oci.Route(services);
        ManualBackupHosting.Configure(services);
        // Bound captured output from the 10,002-snapshot customer workload; warnings and errors remain visible.
        services.AddLogging(logging => logging.AddFilter("System.Net.Http.HttpClient.backup-s3-customer.", LogLevel.Warning));
    }

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));

    private static async Task<ManagedDatabaseRef> CreateManagedDatabaseAsync(ManagementApi api, CancellationToken cancellationToken)
    {
        var organization = await CreateAsync("/v1/management/organizations", "Org " + Guid.NewGuid().ToString("N"));
        var project = await CreateAsync($"/v1/management/organizations/{organization}/projects", "Project");
        var database = await CreateAsync($"/v1/management/organizations/{organization}/projects/{project}/databases", "main");
        return new(organization, project, database, "main");

        async Task<string> CreateAsync(string path, string name)
        {
            using var response = await api.Client.PostAsJsonAsync(Uri(path), new { name }, cancellationToken);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken)).GetProperty("id").GetString()!;
        }
    }

    private static async Task CreateCollectionAsync(HttpClient data, ManagedDatabaseRef database, string collection, CancellationToken cancellationToken)
    {
        using var response = await data.PostAsJsonAsync(Uri($"{database.Data}/collections"), new { name = collection }, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Conflict)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    private static Dictionary<string, string> Destination(string mode) => new(StringComparer.Ordinal)
    {
        ["endpoint"] = FakeOci.CustomerEndpoint,
        ["region"] = "test-region-1",
        ["bucket"] = FakeOci.CustomerBucket,
        ["accessKeyId"] = FakeOci.CustomerAccessKey,
        ["secretKey"] = FakeOci.CustomerSecretKey,
        ["mode"] = mode,
    };

    private static async Task<DateTimeOffset> LatestAsync(HttpClient client, ManagedDatabaseRef database, string destination, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(Uri(database.Path + "/point-in-time?destination=" + destination), cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken)).GetProperty("latest").GetDateTimeOffset();
    }

    private static async Task SetAsync(ManagementApi api, ManagedDatabaseRef database, string mode, CancellationToken cancellationToken)
    {
        using var response = await api.Client.PutAsJsonAsync(Uri(database.ProjectPath + "/backup-destination"), Destination(mode), cancellationToken);
        response.EnsureSuccessStatusCode();
        await AssertDestinationAsync(await response.Content.ReadAsStringAsync(cancellationToken), mode);
    }

    private static async Task AssertDestinationAsync(string json, string mode)
    {
        using var body = JsonDocument.Parse(json);
        await Assert.That(body.RootElement.EnumerateObject().Select(property => property.Name)).IsEquivalentTo(["endpoint", "region", "bucket", "mode", "updatedAt"]);
        await Assert.That(body.RootElement.GetProperty("endpoint").GetString()!.TrimEnd('/')).IsEqualTo(FakeOci.CustomerEndpoint);
        await Assert.That(body.RootElement.GetProperty("region").GetString()).IsEqualTo("test-region-1");
        await Assert.That(body.RootElement.GetProperty("bucket").GetString()).IsEqualTo(FakeOci.CustomerBucket);
        await Assert.That(body.RootElement.GetProperty("mode").GetString()).IsEqualTo(mode);
        await Assert.That(body.RootElement.GetProperty("updatedAt").GetDateTimeOffset()).IsGreaterThan(DateTimeOffset.MinValue);
        await AssertPublicAsync(json);
    }

    private static async Task AssertPublicAsync(string json)
    {
        await Assert.That(json.Contains(FakeOci.CustomerAccessKey, StringComparison.Ordinal)).IsFalse();
        await Assert.That(json.Contains(FakeOci.CustomerSecretKey, StringComparison.Ordinal)).IsFalse();
        await Assert.That(json.Contains("ocid1.vaultsecret", StringComparison.Ordinal)).IsFalse();
    }

    private static async Task ShipAsync(ManagementApi api, FakeTimeProvider time, CancellationToken cancellationToken)
    {
        time.Advance(TimeSpan.FromSeconds(10));
        await api.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(cancellationToken);
    }

    private static async Task PutAsync(HttpClient data, ManagedDatabaseRef database, string name, string text, CancellationToken cancellationToken)
    {
        using var response = await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", name)), new { text }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<ManagedDatabaseRef> RestoreAsync(ManagementApi api, ManagedDatabaseRef database, DateTimeOffset at, string name, string? destination, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object>(StringComparer.Ordinal) { ["pointInTime"] = at, ["name"] = name };
        if (destination is not null)
        {
            body["destination"] = destination;
        }

        using var response = await api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/restore"), body, cancellationToken);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var restored = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        await Assert.That(restored.GetProperty("branchedFrom").GetString()).IsEqualTo(database.Id);
        return new(database.Organization, database.Project, restored.GetProperty("id").GetString()!, restored.GetProperty("name").GetString()!);
    }

    private static async Task AssertDocumentAsync(HttpClient data, ManagedDatabaseRef database, string name, string text, CancellationToken cancellationToken)
    {
        using var response = await data.GetAsync(Uri(ManagementApi.DocumentPath(database, "people", name)), cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        await Assert.That(body.GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);
    }

    private static string File(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}";

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
