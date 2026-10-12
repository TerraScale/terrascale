using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Fast.Store;

/// <summary>
/// No startup order between ts-auth and TerraBase: ts-auth starts without TerraBase, answers 503 on <c>/readyz</c>, and
/// becomes ready by itself once TerraBase serves; a TerraBase crash and restart while ts-auth runs is the same. Each test
/// owns its TerraBase process (the one built from this commit), so it can stop it.
/// </summary>
public sealed class StorageRecoveryTests
{
    private static readonly TimeSpan Recovery = TimeSpan.FromSeconds(20);

    [Test]
    public async Task Ts_auth_started_before_terrabase_becomes_ready_once_terrabase_serves()
    {
        var (running, unavailable) = await TerraBaseTestServer.StartAsync();
        await using var terrabase = running ?? throw new InvalidOperationException(unavailable);
        var tenant = await terrabase.CreateTenantAsync();
        await terrabase.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth").CreateAsync();
        await terrabase.StopAsync();

        // A configured client is registered by the storage bootstrap too, so it cannot stop startup either.
        var client = AuthStore.NewId();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var auth = await StartAuthAsync(terrabase, tenant, $"{client}:{secret}");
        await Task.Delay(ReadinessWait.NextPass);
        using (var down = await auth.Client.GetAsync("/readyz"))
        {
            await Assert.That(down.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        }

        await terrabase.RestartAsync();
        await Assert.That(await ReadinessWait.ReadyWithinAsync(auth.Client, Recovery)).IsTrue();
        // The bootstrap that ran once TerraBase answered created the collections and the first signing key.
        var database = terrabase.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth");
        await Assert.That((await database.ListCollectionsAsync(limit: 200)).Items.Count).IsEqualTo(AuthStore.RequiredCollections.Count);
        await Assert.That((await database.ListDocumentsAsync(AuthStore.SigningKeyCollection)).Items.Count).IsEqualTo(1);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/{OidcIssuer.Slug}/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{client}:{secret}")));
        using var token = await auth.Client.SendAsync(request);
        await Assert.That(token.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()).IsNotNull();
    }

    [Test]
    public async Task A_terrabase_crash_while_ts_auth_runs_is_not_ready_then_recovers_with_the_same_key()
    {
        var (running, unavailable) = await TerraBaseTestServer.StartAsync();
        await using var terrabase = running ?? throw new InvalidOperationException(unavailable);
        var tenant = await terrabase.CreateTenantAsync();
        await terrabase.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth").CreateAsync();
        await using var auth = await StartAuthAsync(terrabase, tenant);
        await Assert.That(await ReadinessWait.ReadyWithinAsync(auth.Client, Recovery)).IsTrue();
        var before = await KeyIdAsync(auth);

        await terrabase.StopAsync();
        await Assert.That(await ReadinessWait.StatusWithinAsync(auth.Client, "not_ready", Recovery)).IsTrue();

        await terrabase.RestartAsync();
        await Assert.That(await ReadinessWait.ReadyWithinAsync(auth.Client, Recovery)).IsTrue();
        await Assert.That(await KeyIdAsync(auth)).IsEqualTo(before);
        var keys = await terrabase.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth").ListDocumentsAsync(AuthStore.SigningKeyCollection);
        await Assert.That(keys.Items.Count).IsEqualTo(1);
    }

    private static async Task<string> KeyIdAsync(AuthTestServer auth)
    {
        var jwks = await auth.Client.GetFromJsonAsync<JsonElement>($"/{OidcIssuer.Slug}/.well-known/jwks.json");
        return jwks.GetProperty("keys").EnumerateArray().Single().GetProperty("kid").GetString()!;
    }

    private static Task<AuthTestServer> StartAuthAsync(TerraBaseTestServer terrabase, TerraBaseTestTenant tenant, string? serviceClients = null) =>
        AuthTestServer.StartAsync(settings: values =>
        {
            if (serviceClients is not null)
            {
                values["service-clients"] = serviceClients;
            }
        }, services: services =>
        {
            services.AddSingleton(terrabase.Http);
            services.AddSingleton(_ => terrabase.InternalDocuments().Database(tenant.Project.Id, "auth"));
        });
}
