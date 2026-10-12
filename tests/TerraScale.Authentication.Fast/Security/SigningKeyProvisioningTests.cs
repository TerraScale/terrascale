using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Security;

/// <summary>
/// Fail closed: ts-auth never creates the platform issuer's signing key itself. Without keys it stays not ready with a
/// clear error until the operator command provisions one; existing keys survive restarts untouched; unusable keys are
/// never replaced. Synthetic master keys and an isolated database per test.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class SigningKeyProvisioningTests(TerraBaseFixture fixture)
{
    private const string NoKeys = "no signing keys: operator provisioning required";
    private static readonly TimeSpan Recovery = TimeSpan.FromSeconds(20);

    [Test]
    public async Task Without_keys_ts_auth_stays_not_ready_with_a_clear_error_until_the_operator_provisions_them()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using var server = await StartAsync(target, masterKey);

        await Assert.That(await ReadinessWait.UntilAsync(async () => await ErrorAsync(server) == NoKeys + " (docs/auth/signing-keys.md)", Recovery)).IsTrue();
        using (var jwks = await server.Client.GetAsync($"/{OidcIssuer.Slug}/.well-known/jwks.json"))
        {
            await Assert.That(jwks.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        }

        // Neither the bootstrap's retries, the readiness passes nor the JWKS request created a key.
        await Task.Delay(ReadinessWait.NextPass);
        await Assert.That(await StoredAsync(target)).IsEmpty();

        await Assert.That(await SigningKeyProvisioning.RunAsync(server.App.Services, CancellationToken.None)).IsEqualTo(0);
        await Assert.That(await ReadinessWait.ReadyWithinAsync(server.Client, Recovery)).IsTrue();
        await Assert.That((await StoredAsync(target)).Count).IsEqualTo(1);
        await Assert.That(await ErrorAsync(server)).IsNull();
    }

    [Test]
    public async Task Existing_keys_are_kept_across_restarts_and_provisioning_again_changes_nothing()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string before;
        await using (var server = await StartAsync(target, masterKey, provision: true))
        {
            await Assert.That(await ReadinessWait.ReadyWithinAsync(server.Client, Recovery)).IsTrue();
            before = (await StoredAsync(target)).Single().ToJsonString();
        }

        await using var restarted = await StartAsync(target, masterKey);
        await Assert.That(await ReadinessWait.ReadyWithinAsync(restarted.Client, Recovery)).IsTrue();
        await Assert.That(await SigningKeyProvisioning.ProvisionAsync(restarted.App.Services, CancellationToken.None)).IsFalse();
        await Assert.That((await StoredAsync(target)).Single().ToJsonString()).IsEqualTo(before);
    }

    [Test]
    public async Task Tampered_keys_fail_closed_and_are_never_replaced()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using (var server = await StartAsync(target, masterKey, provision: true))
        {
            await Assert.That(await ReadinessWait.ReadyWithinAsync(server.Client, Recovery)).IsTrue();
        }

        // A storage writer can copy a key under another kid, but cannot recompute its public-metadata MAC.
        var record = (await StoredAsync(target)).Single();
        record["id"] = AuthStore.NewId();
        await target.PutAsync(AuthStore.SigningKeyCollection, OidcIssuer.InstanceId, record["id"]!.GetValue<string>(), JsonSerializer.SerializeToElement(record));
        var tampered = Json(await StoredAsync(target));

        await using var restarted = await StartAsync(target, masterKey);
        await Task.Delay(ReadinessWait.NextPass);
        using (var ready = await restarted.Client.GetAsync("/readyz"))
        {
            await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        }

        await Assert.That(await SigningKeyProvisioning.RunAsync(restarted.App.Services, CancellationToken.None)).IsEqualTo(1);
        await Assert.That(Json(await StoredAsync(target))).IsEqualTo(tampered);
    }

    private static Task<AuthTestServer> StartAsync(SharedStateTarget target, string masterKey, bool provision = false) => AuthTestServer.StartAsync(
        settings: values => values["master-key"] = masterKey,
        services: services => target.Register(services),
        beforeStart: provision ? services => SigningKeyProvisioning.ProvisionAsync(services, CancellationToken.None) : null);

    private static async Task<string?> ErrorAsync(AuthTestServer server)
    {
        using var response = await server.Client.GetAsync("/readyz");
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    private static string Json(List<JsonObject> keys) => string.Join('\n', keys.Select(key => key.ToJsonString()).Order(StringComparer.Ordinal));

    private static async Task<List<JsonObject>> StoredAsync(SharedStateTarget target) =>
        [.. (await target.Database.ListDocumentsAsync(AuthStore.SigningKeyCollection)).Items
            .Select(document => JsonNode.Parse(document.Document.GetRawText())!.AsObject())
            .Where(document => document["instanceId"]!.GetValue<string>() == OidcIssuer.InstanceId)];
}
