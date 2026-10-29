using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TerraBase.Features.Platform;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

/// <summary>
/// Platform admins (a ts-auth user token with platform_role admin, forwarded by the dashboard) read the platform databases
/// through the explore API: read-only, secrets redacted, every read logged with its actor. Nobody else does.
/// </summary>
public sealed class PlatformExploreTests
{
    private static readonly string Keys = $"/v1/projects/{PlatformDatabases.AuthProjectId}/databases/{PlatformDatabases.Auth}/collections/signing_keys";
    private static readonly string Explore = $"{PlatformExplore.Databases}/{PlatformDatabases.Auth}/collections/signing_keys/documents";

    [Test]
    public async Task Admins_read_the_platform_databases_with_secrets_redacted_and_every_read_is_logged()
    {
        var logs = new ExploreLogs();
        await using var api = await StartAsync(logs);
        await WriteKeyAsync(api);
        var admin = EntityIds.New().ToString();
        using var client = api.WithToken(AdminToken(api, admin));

        // The seed creates the auth database, which WriteKeyAsync waited for, before the dashboard one. Another admin
        // waits, so the reads counted below are only this test's four.
        using var waiter = api.WithToken(AdminToken(api, EntityIds.New().ToString()));
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            using var seeded = await waiter.GetAsync(ManagementApi.Uri(PlatformExplore.Databases), token);
            return (await seeded.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("items").GetArrayLength() == 2;
        });
        using var databases = await client.GetAsync(ManagementApi.Uri(PlatformExplore.Databases));
        using var collections = await client.GetAsync(ManagementApi.Uri($"{PlatformExplore.Databases}/{PlatformDatabases.Auth}/collections"));
        using var page = await client.GetAsync(ManagementApi.Uri(Explore));
        using var one = await client.GetAsync(ManagementApi.Uri(Explore + "/by-key?id=" + ManagementApi.DocumentId("k1")));
        var listed = (await databases.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("name").GetString()!).ToList();
        var key = (await one.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document");
        var paged = (await page.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0].GetProperty("document");

        await Assert.That(listed).IsEquivalentTo([PlatformDatabases.Auth, PlatformDatabases.Dashboard]);
        await Assert.That(await collections.Content.ReadAsStringAsync()).Contains("signing_keys");
        foreach (var document in new[] { key, paged })
        {
            // Public fields stay; sealed key material, private key parameters and hashes never leave.
            await Assert.That(document.GetProperty("kid").GetString()).IsEqualTo("k1");
            await Assert.That(document.GetProperty("x").GetString()).IsEqualTo("public-x");
            await Assert.That(document.GetProperty("secretBox").GetString()).IsEqualTo(PlatformExplore.Redacted);
            await Assert.That(document.GetProperty("d").GetString()).IsEqualTo(PlatformExplore.Redacted);
            await Assert.That(document.GetProperty("owner").GetProperty("password_hash").GetString()).IsEqualTo(PlatformExplore.Redacted);
            await Assert.That(document.GetProperty("owner").GetProperty("email").GetString()).IsEqualTo("ada@example.com");
        }

        var body = await one.Content.ReadAsStringAsync();
        await Assert.That(body).DoesNotContain("sealed-private-key");
        await Assert.That(body).DoesNotContain("private-d");
        await Assert.That(logs.Entries.Count(entry => entry.Contains($"Platform admin {admin} read", StringComparison.Ordinal))).IsEqualTo(4);
        await Assert.That(logs.Entries.Any(entry => entry.Contains("sealed-private-key", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Admins_explore_the_dashboard_database_in_its_own_project()
    {
        await using var api = await StartAsync(new ExploreLogs());
        using var storage = PeerTls.CreateClient(api.Server.BaseAddress);
        const string dashboard = "/v1/projects/00000000000000000000000005/databases/ts-dashboard/collections";
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            using var created = await storage.PostAsJsonAsync(ManagementApi.Uri(dashboard), new { name = "plans" }, token);
            return created.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict;
        });
        using var written = await storage.PutAsJsonAsync(ManagementApi.Uri(dashboard + "/plans/documents/by-key?id=" + ManagementApi.DocumentId("basic")), new { name = "Basic" });
        written.EnsureSuccessStatusCode();
        using var client = api.WithToken(AdminToken(api, EntityIds.New().ToString()));
        using var read = await client.GetAsync(ManagementApi.Uri(PlatformExplore.Databases + "/ts-dashboard/collections/plans/documents/by-key?id=" + ManagementApi.DocumentId("basic")));

        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document").GetProperty("name").GetString()).IsEqualTo("Basic");
    }

    private static string AdminToken(ManagementApi api, string user) =>
        api.Issuer.Mint(user, token => token.Claims[PlatformExplore.RoleClaim] = PlatformExplore.AdminRole);

    private static Task<ManagementApi> StartAsync(ExploreLogs logs) => ManagementApi.StartAsync(
        settings => settings[TerraBaseTestServer.PlatformSeedKey] = "true",
        services => TerraBaseTestServer.CaptureDebugLogs(services, logs));

    // A ts-auth signing key as the operator stores it (shape of ts-auth's signing_keys documents, synthetic values).
    private static async Task WriteKeyAsync(ManagementApi api)
    {
        using var storage = PeerTls.CreateClient(api.Server.BaseAddress);
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            using var created = await storage.PostAsJsonAsync(ManagementApi.Uri(Keys[..Keys.LastIndexOf('/')]), new { name = "signing_keys" }, token);
            return created.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict;
        });
        using var written = await storage.PutAsJsonAsync(ManagementApi.Uri(Keys + "/documents/by-key?id=" + ManagementApi.DocumentId("k1")), new
        {
            kid = "k1",
            algorithm = "ES256",
            x = "public-x",
            d = "private-d",
            secretBox = "sealed-private-key",
            owner = new { email = "ada@example.com", password_hash = "argon2id-hash" },
        });
        written.EnsureSuccessStatusCode();
    }

    private sealed class ExploreLogs : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue(formatter(state, exception));

        public void Dispose()
        {
        }
    }
}
