using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Fast.Oidc;

/// <summary>Real HTTP minting with authoritative API-key storage through the SDK and same-commit TerraBase.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class EndUserAccessTokenTests(TerraBaseFixture fixture)
{
    private const string Audience = "https://api.terrascale.tech";
    private static readonly string[] ReadWrite = ["documents:read", "documents:write"];
    private static readonly string[] ReadOnly = ["documents:read"];
    private static readonly string[] WriteOnly = ["documents:write"];
    private static readonly string[] ExpectedRoles = ["reader", "Reader"];
    private static readonly string[] AssertedRoles = ["reader", "Reader", "reader"];

    [Test]
    public async Task A_backend_asserts_an_unprovisioned_identity_and_roles_and_revocation_stops_future_minting()
    {
        await using var oidc = await this.StartAsync();
        var (owner, organization, project) = await SetupAsync(oidc);
        var key = await KeyAsync(oidc, owner, organization, project);
        var user = AuthStore.NewId();
        var body = new { user_id = user, claims = new { roles = AssertedRoles } };
        using var minted = await oidc.CallAsync(HttpMethod.Post, Path(project), key, body);
        await Assert.That(minted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(minted.Headers.CacheControl!.NoStore).IsTrue();
        await Assert.That(minted.Headers.Contains("Set-Cookie")).IsFalse();
        var response = await OidcHarness.JsonAsync(minted);
        await Assert.That(response.GetProperty("token_type").GetString()).IsEqualTo("Bearer");
        await Assert.That(response.GetProperty("expires_in").GetInt64()).IsEqualTo(900);
        var encoded = response.GetProperty("access_token").GetString()!;
        var jwt = await oidc.ValidateAsync(encoded, Audience);
        await Assert.That(jwt.Subject).IsEqualTo(user);
        await Assert.That(jwt.Typ).IsEqualTo("at+jwt");
        await Assert.That(jwt.Alg).IsEqualTo("ES256");
        await Assert.That(string.IsNullOrEmpty(jwt.Kid)).IsFalse();
        await Assert.That(jwt.GetPayloadValue<string>("project_id")).IsEqualTo(project);
        await Assert.That(jwt.GetPayloadValue<string>("instance_id")).IsEqualTo(project);
        await Assert.That(jwt.GetPayloadValue<string>("token_kind")).IsEqualTo("end_user");
        await Assert.That(jwt.GetPayloadValue<string[]>("roles")).IsEquivalentTo(ExpectedRoles);
        foreach (var claim in new[] { "scope", "scopes", "database", "platform_role", "org_id" })
        {
            await Assert.That(jwt.TryGetPayloadValue<object>(claim, out _)).IsFalse().Because(claim);
        }

        await Assert.That(jwt.ValidTo - jwt.IssuedAt).IsEqualTo(EndUserAccessTokens.Lifetime);
        // Warm all authority/signing caches before revocation.
        await oidc.OkAsync(HttpMethod.Post, Path(project), key, body);
        await oidc.OkAsync(HttpMethod.Delete, $"{Keys(organization, project)}/{key[..26]}", owner, status: HttpStatusCode.NoContent);
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, Path(project), key, body)).IsEqualTo((401, "unauthorized"));
        // Offline JWT validation does not contact ts-auth to check key revocation.
        await Assert.That((await oidc.ValidateAsync(encoded, Audience)).Subject).IsEqualTo(user);
    }



    [Test]
    public async Task Foreign_org_narrowed_tampered_platform_and_anonymous_credentials_cannot_mint()
    {
        await using var oidc = await this.StartAsync();
        var (owner, organization, project) = await SetupAsync(oidc);
        var other = await oidc.CreateProjectAsync(owner, organization, "Other");
        var key = await KeyAsync(oidc, owner, organization, project);
        var tampered = key[..^1] + (key[^1] == '0' ? '1' : '0');
        var organizationKey = await KeyAsync(oidc, owner, organization, null);
        var read = await KeyAsync(oidc, owner, organization, project, scopes: ReadOnly);
        var write = await KeyAsync(oidc, owner, organization, project, scopes: WriteOnly);
        var narrowed = await KeyAsync(oidc, owner, organization, project, database: await oidc.CreateDatabaseIdAsync("app"));
        foreach (var credential in new[] { tampered, organizationKey, read, write, narrowed, owner, null })
        {
            await Assert.That(await oidc.FailAsync(HttpMethod.Post, Path(project), credential, new { user_id = AuthStore.NewId() }))
                .IsEqualTo((401, "unauthorized"));
        }

        await Assert.That(await oidc.FailAsync(HttpMethod.Post, Path(other), key, new { user_id = AuthStore.NewId() })).IsEqualTo((401, "unauthorized"));
        using var cookieRequest = new HttpRequestMessage(HttpMethod.Post, Path(project)) { Content = new StringContent("{", Encoding.UTF8, "application/json") };
        cookieRequest.Headers.Add("Cookie", "tsauth_session=" + key);
        using var cookie = await oidc.Client.SendAsync(cookieRequest);
        await Assert.That(cookie.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(cookie.Headers.WwwAuthenticate.Single().Scheme).IsEqualTo("Bearer");
    }







    private Task<OidcHarness> StartAsync(TimeProvider? clock = null) => OidcHarness.StartAsync(fixture, values => values["access-token-audiences"] = Audience, clock: clock);

    private static async Task<(string Owner, string Organization, string Project)> SetupAsync(OidcHarness oidc)
    {
        var (_, owner) = await oidc.SignInUserAsync("owner@example.test");
        var organization = await oidc.CreateOrganizationAsync(owner);
        return (owner, organization, await oidc.CreateProjectAsync(owner, organization, "Production"));
    }

    private static string Path(string project) => $"/v1/terrascale/projects/{project}/end-user-tokens";

    private static string Keys(string organization, string? project) => $"{Organizations}/{organization}" + (project is null ? string.Empty : $"/projects/{project}") + "/api-keys";

    private static string[] LargeRoles(int characters, char value) => Enumerable.Range(0, 64)
        .Select(index => index.ToString("D2", CultureInfo.InvariantCulture) + new string(value, Math.Clamp(characters - index * 126, 0, 126))).ToArray();

    private static async Task<string> KeyAsync(OidcHarness oidc, string owner, string organization, string? project, string? database = null, string[]? scopes = null, DateTimeOffset? expires = null) =>
        (await oidc.OkAsync(HttpMethod.Post, Keys(organization, project), owner,
            new { name = "backend", scopes = scopes ?? ReadWrite, database, expires_at = expires }, HttpStatusCode.Created)).GetProperty("key").GetString()!;

    private sealed class AfterContinueContent(byte[] body, Func<Task> beforeWrite) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await beforeWrite();
            await stream.WriteAsync(body);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = body.Length;
            return true;
        }
    }
}
