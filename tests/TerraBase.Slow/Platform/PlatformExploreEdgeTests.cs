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
public sealed class PlatformExploreEdgeTests
{
    private static readonly string Keys = $"/v1/projects/{PlatformDatabases.AuthProjectId}/databases/{PlatformDatabases.Auth}/collections/signing_keys";
    private static readonly string Explore = $"{PlatformExplore.Databases}/{PlatformDatabases.Auth}/collections/signing_keys/documents";

    [Test]
    public async Task Everyone_but_a_platform_admin_is_refused_and_nothing_is_writable()
    {
        await using var api = await StartAsync(new ExploreLogs());
        await WriteKeyAsync(api);
        var user = EntityIds.New().ToString();
        using var plainUser = api.WithToken(api.Issuer.Mint(user));
        using var viewer = api.WithToken(api.Issuer.Mint(user, token => token.Claims[PlatformExplore.RoleClaim] = "viewer"));
        // A project token carries no platform role, whatever it claims.
        using var projectToken = api.WithToken(api.Issuer.Mint(user, token =>
        {
            token.Claims[PlatformExplore.RoleClaim] = PlatformExplore.AdminRole;
            token.Claims["project_id"] = PlatformDatabases.AuthProjectId;
            token.Claims["scope"] = "documents:read";
        }));
        using var anonymous = api.WithToken(null);
        using var admin = api.WithToken(AdminToken(api, user));
        var path = ManagementApi.Uri(Explore + "/by-key?id=" + ManagementApi.DocumentId("k1"));

        using var asUser = await plainUser.GetAsync(path);
        using var asViewer = await viewer.GetAsync(path);
        using var asProject = await projectToken.GetAsync(path);
        using var asNobody = await anonymous.GetAsync(path);
        using var asOperator = await api.Service(user).GetAsync(path);
        using var otherDatabase = await admin.GetAsync(ManagementApi.Uri($"{PlatformExplore.Databases}/some-customer-db/collections"));
        using var write = await admin.PutAsJsonAsync(path, new { kid = "k2" });

        await Assert.That(asUser.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(asViewer.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(asProject.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(asNobody.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(asOperator.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden).IsTrue();
        await Assert.That(otherDatabase.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        // No write route exists: the method is refused and the stored key is unchanged.
        await Assert.That(write.IsSuccessStatusCode).IsFalse();
        using var storage = PeerTls.CreateClient(api.Server.BaseAddress);
        using var stored = await storage.GetAsync(ManagementApi.Uri(Keys + "/documents/by-key?id=" + ManagementApi.DocumentId("k1")));
        await Assert.That((await stored.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document").GetProperty("kid").GetString()).IsEqualTo("k1");
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
