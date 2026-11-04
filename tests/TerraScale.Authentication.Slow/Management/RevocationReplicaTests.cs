using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Fast.Management;

/// <summary>
/// The platform management API (TS-598): organizations, members, projects and project roles, over real HTTP on the
/// real TerraBase, as signed-in users.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class RevocationReplicaTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task A_sign_in_revoked_on_one_replica_is_refused_at_once_on_another()
    {
        // Each replica reads the sign-in from TerraBase on every call; nothing it saw earlier is reused.
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await using var replica = await oidc.StartReplicaAsync();
        var (_, _, tokens) = await oidc.SignInAndRedeemAsync("alice@example.test");
        var access = tokens.GetProperty("access_token").GetString()!;
        await oidc.OkAsync(HttpMethod.Get, Organizations, access);
        await oidc.OkAsync(HttpMethod.Get, Organizations, access);

        using (var revoke = new HttpRequestMessage(HttpMethod.Post, "/terrascale/oauth/revoke")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = tokens.GetProperty("refresh_token").GetString()!, ["token_type_hint"] = "refresh_token" }),
        })
        {
            revoke.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"01M4EQ2FRJ8S8B29E8TQVWNW4J:{OidcHarness.ClientSecret}")));
            using var revoked = await replica.Client.SendAsync(revoke);
            await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        await Assert.That(await oidc.FailAsync(HttpMethod.Get, Organizations, access)).IsEqualTo((401, "unauthorized"));
    }
}
