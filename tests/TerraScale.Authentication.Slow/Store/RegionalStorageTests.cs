using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Features.Webhooks;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Slow.Store;

/// <summary>Every storage operation uses the local region and fails closed when that backend is unavailable.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class RegionalStorageTests(TerraBaseFixture fixture)
{
    private const string Instance = "regional-instance";
    private const string Local = "auth-local";

    [Test]
    public async Task While_the_local_region_is_down_every_request_fails_closed()
    {
        var (tenant, designated) = await fixture.CreateDatabaseAsync();
        var local = await fixture.CreateCopyAsync(tenant, Local);
        await UsersIn(local).CreateAsync(Instance, "local-only@example.test");
        await UsersIn(designated).CreateAsync(Instance, "known@example.test");
        var project = await ProjectAsync(local);
        await using var gru = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var own = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var other = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var auth = await StartAsync(tenant, own.Address);
        await KeyAsync(auth, local);
        await Assert.That(await ReadinessWait.ReadyWithinAsync(auth.Client, TimeSpan.FromSeconds(15))).IsTrue();

        own.Stop();
        using var refused = await auth.Client.PostAsync("/__test/users/new@example.test", null);

        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var error = (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("temporarily_unavailable");
        await Assert.That(await UsersIn(local).FindByEmailAsync(Instance, "new@example.test")).IsNull();
        await Assert.That(await UsersIn(designated).FindByEmailAsync(Instance, "new@example.test")).IsNull();
        // Neither stale local state nor a healthy remote copy bypasses the unavailable local backend.
        await Assert.That(await UserAsync(auth, "local-only@example.test")).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(await UserAsync(auth, "known@example.test")).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using var jwks = await auth.Client.GetAsync($"/{OidcIssuer.Slug}/.well-known/jwks.json");
        await Assert.That(jwks.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(await DiscoveryAsync(auth, project)).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(other.Accepted).IsEqualTo(0);
        // Ready requires the local backend to take writes; a ready replica flips after three consecutive failed passes.
        await Assert.That(await ReadinessWait.StatusWithinAsync(auth.Client, "not_ready", TimeSpan.FromSeconds(45))).IsTrue();
    }

    private static readonly JsonElement Record = TerraBaseFixture.Json($$"""{"instanceId":"{{Instance}}"}""");

    private static Users UsersIn(TerraBaseSqlDatabase copy)
    {
        var store = new AuthStore(copy, TimeProvider.System);
        var options = AuthOptions.Load(AuthTestServer.DefaultSettings());
        return new Users(store, new WebhookOutbox(new WebhookEndpoints(store, options, TimeProvider.System), TimeProvider.System), TimeProvider.System);
    }

    // A project of the issuer's instance in the copy: its tenant's discovery document is served while it exists.
    private static async Task<string> ProjectAsync(TerraBaseSqlDatabase copy)
    {
        var id = AuthStore.NewId();
        await new AuthStore(copy, TimeProvider.System).CreateAsync(AuthStore.ProjectTable, OidcIssuer.InstanceId, id, Record);
        return id;
    }

    // Provisions a signing key in the copy, under the server's master key; returns its key id.
    private static async Task<string> KeyAsync(AuthTestServer auth, TerraBaseSqlDatabase copy)
    {
        await new KeyRing(new AuthStore(copy, TimeProvider.System), auth.App.Services.GetRequiredService<AuthOptions>(), TimeProvider.System)
            .ProvisionAsync(OidcIssuer.InstanceId, CancellationToken.None);
        return (await copy.RowsAsync(AuthStore.SigningKeyTable)).Single().Body.GetProperty("id").GetString()!;
    }

    private static async Task<HttpStatusCode> DiscoveryAsync(AuthTestServer auth, string project)
    {
        using var response = await auth.Client.GetAsync($"/{project}/.well-known/openid-configuration");
        return response.StatusCode;
    }

    private static async Task<HttpStatusCode> UserAsync(AuthTestServer auth, string email)
    {
        using var response = await auth.Client.GetAsync($"/__test/users/{email}");
        return response.StatusCode;
    }

    private static Task<AuthTestServer> StartAsync(TerraBaseTestTenant tenant, Uri ownRegion,
        TerraBaseTransport.HostResolver? resolve = null) =>
        AuthTestServer.StartAsync(
            services: services => TerraBaseFixture.UseRegionalStorage(services, tenant, ownRegion, localDatabase: Local, resolve: resolve),
            // The operator's signing-key provisioning runs first, so /readyz shows whether the local region takes writes.
            beforeStart: services => SigningKeyProvisioning.ProvisionAsync(services, CancellationToken.None),
            routes: app =>
            {
                app.MapGet("/__test/users/{email}", FindAsync);
                app.MapPost("/__test/users/{email}", async (string email, Users users) =>
                    await users.CreateAsync(Instance, email) is not null ? Results.StatusCode(StatusCodes.Status201Created) : Results.Conflict());
            });

    private static async Task<IResult> FindAsync(string email, Users users) =>
        await users.FindByEmailAsync(Instance, email) is not null ? Results.Ok() : Results.NotFound();
}
