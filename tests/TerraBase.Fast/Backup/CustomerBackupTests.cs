using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class CustomerBackupTests
{
    [Test]
    public async Task Both_destinations_ship_encrypted_snapshots_and_wal_and_restore_actual_documents()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => ManualShipping(s3, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await SetAsync(api, database, "both");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var faker = new Faker();
        var before = faker.Lorem.Sentence(12);
        var after = faker.Lorem.Sentence(12);
        await PutAsync(data, database, "before", before);
        await ShipAsync(api, time);
        var snapshot = time.GetUtcNow();
        await PutAsync(data, database, "after", after);
        await ShipAsync(api, time);

        foreach (var destination in new[] { "platform", "customer" })
        {
            using var window = await api.Client.GetAsync(Uri($"{database.Path}/point-in-time?destination={destination}"), cancellationToken);
            window.EnsureSuccessStatusCode();
            var bounds = await window.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            await Assert.That(bounds.GetProperty("earliest").GetDateTimeOffset()).IsEqualTo(snapshot);
            var latest = bounds.GetProperty("latest").GetDateTimeOffset();
            var restored = await RestoreAsync(api, database, latest, "latest-" + destination, destination);
            await AssertDocumentAsync(data, restored, "before", before);
            await AssertDocumentAsync(data, restored, "after", after);
            var earlier = await RestoreAsync(api, database, snapshot, "earlier-" + destination, destination);
            await AssertDocumentAsync(data, earlier, "before", before);
            using var absent = await data.GetAsync(Uri(ManagementApi.DocumentPath(earlier, "people", "after")), cancellationToken);
            await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        foreach (var destination in new string?[] { null, "", "platform" })
        {
            var query = destination is null ? "" : "?destination=" + destination;
            using var defaultWindow = await api.Client.GetAsync(Uri(database.Path + "/point-in-time" + query), cancellationToken);
            defaultWindow.EnsureSuccessStatusCode();
            var defaultBounds = await defaultWindow.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            var defaultRestore = await RestoreAsync(api, database, defaultBounds.GetProperty("latest").GetDateTimeOffset(),
                "default-" + (destination is null ? "omitted" : destination.Length == 0 ? "empty" : destination), destination);
            await AssertDocumentAsync(data, defaultRestore, "after", after);
        }
        var customerKeys = s3.CustomerObjects.Keys.ToArray();
        await Assert.That(customerKeys.Any(key => key.StartsWith(File(database) + "/snapshots/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(customerKeys.Any(key => key.StartsWith(File(database) + "/wal/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(customerKeys.All(s3.Objects.ContainsKey)).IsTrue();
        await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith("v1/_system/nodes/local/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(customerKeys.Any(key => key.Contains("_system", StringComparison.Ordinal))).IsFalse();
        await Assert.That(s3.CustomerObjects.All(item => item.Value.AsSpan().StartsWith("TBK1"u8) || item.Key.StartsWith("v1/keys/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(s3.CustomerObjects.Values.Any(content => content.AsSpan().IndexOf(Encoding.UTF8.GetBytes(after)) >= 0)).IsFalse();
        await Assert.That(s3.BadSignatures).IsEqualTo(0);
    }

    private sealed class StalledReadStream : MemoryStream
    {
        private readonly TaskCompletionSource<int> _body = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanSeek => false;

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try
            {
                return await _body.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }

        public void Release() => _body.TrySetResult(0);
    }

    private static void ManualShipping(FakeS3 s3, IServiceCollection services)
    {
        s3.Route(services);
        ManualBackupHosting.Configure(services);
    }

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));

    private static Dictionary<string, string> Destination(string mode) => new(StringComparer.Ordinal)
    {
        ["endpoint"] = FakeS3.CustomerEndpoint,
        ["region"] = "test-region-1",
        ["bucket"] = FakeS3.CustomerBucket,
        ["accessKeyId"] = FakeS3.CustomerAccessKey,
        ["secretKey"] = FakeS3.CustomerSecretKey,
        ["mode"] = mode,
    };

    private static HttpClient ProjectClient(ManagementApi api, string project, string scope, string? database = null) =>
        api.WithToken(api.Issuer.Mint(Ulid.NewUlid().ToString(), token =>
        {
            token.Claims["project_id"] = project;
            token.Claims["scope"] = scope;
            if (database is not null)
            {
                token.Claims["database"] = database;
            }
        }));

    private static async Task<DateTimeOffset> LatestAsync(HttpClient client, ManagedDatabaseRef database, string destination = "platform")
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var response = await client.GetAsync(Uri(database.Path + "/point-in-time?destination=" + destination), cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken)).GetProperty("latest").GetDateTimeOffset();
    }

    private static async Task SetAsync(ManagementApi api, ManagedDatabaseRef database, string mode)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var response = await api.Client.PutAsJsonAsync(Uri(database.ProjectPath + "/backup-destination"), Destination(mode), cancellationToken);
        response.EnsureSuccessStatusCode();
        await AssertDestinationAsync(await response.Content.ReadAsStringAsync(cancellationToken), mode);
    }

    private static async Task AssertDestinationAsync(string json, string mode)
    {
        using var body = JsonDocument.Parse(json);
        await Assert.That(body.RootElement.EnumerateObject().Select(property => property.Name)).IsEquivalentTo(["endpoint", "region", "bucket", "mode", "updatedAt"]);
        await Assert.That(body.RootElement.GetProperty("endpoint").GetString()!.TrimEnd('/')).IsEqualTo(FakeS3.CustomerEndpoint);
        await Assert.That(body.RootElement.GetProperty("region").GetString()).IsEqualTo("test-region-1");
        await Assert.That(body.RootElement.GetProperty("bucket").GetString()).IsEqualTo(FakeS3.CustomerBucket);
        await Assert.That(body.RootElement.GetProperty("mode").GetString()).IsEqualTo(mode);
        await Assert.That(body.RootElement.GetProperty("updatedAt").GetDateTimeOffset()).IsGreaterThan(DateTimeOffset.MinValue);
        await AssertPublicAsync(json);
    }

    private static async Task AssertPublicAsync(string json)
    {
        await Assert.That(json.Contains(FakeS3.CustomerAccessKey, StringComparison.Ordinal)).IsFalse();
        await Assert.That(json.Contains(FakeS3.CustomerSecretKey, StringComparison.Ordinal)).IsFalse();
        await Assert.That(json.Contains("wrappedKey", StringComparison.Ordinal)).IsFalse();
    }

    private static async Task ShipAsync(ManagementApi api, FakeTimeProvider time, CancellationToken? cancellationToken = null)
    {
        time.Advance(TimeSpan.FromSeconds(10));
        await api.Server.Services.GetRequiredService<IContinuousBackup>()
            .RunOnceAsync(cancellationToken ?? TUnit.Core.TestContext.Current!.Execution.CancellationToken);
    }

    private static async Task PutAsync(HttpClient data, ManagedDatabaseRef database, string name, string text)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var response = await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", name)), new { text }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<ManagedDatabaseRef> RestoreAsync(ManagementApi api, ManagedDatabaseRef database, DateTimeOffset at, string name, string? destination = null)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
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

    private static async Task AssertDocumentAsync(HttpClient data, ManagedDatabaseRef database, string name, string text)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var response = await data.GetAsync(Uri(ManagementApi.DocumentPath(database, "people", name)), cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        await Assert.That(body.GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);
    }

    private static HashSet<string> Generations(IReadOnlyDictionary<string, byte[]> objects, ManagedDatabaseRef database) =>
        objects.Keys.Where(key => key.StartsWith(File(database) + "/snapshots/", StringComparison.Ordinal))
            .Select(key => key.Split('/')[^1].Split('-')[1]).ToHashSet(StringComparer.Ordinal);

    private static string File(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local";

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
