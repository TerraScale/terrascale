using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TerraScale.Authentication.Tests;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Fast.Management;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class ScopedKeyTests(TerraBaseFixture fixture)
{
    private const string Audience = "https://api.terrascale.tech";
    private const string Mint = "/v1/terrascale/api-keys/self/scoped-keys";
    private static readonly string[] Orchestrator = ["databases:write", "sql:read", "sql:write"];
    private static readonly string[] ReadOnly = ["sql:read"];

    [Test]
    public async Task A_project_key_mints_a_read_only_key_for_one_database_whose_token_reaches_only_it()
    {
        await using var oidc = await StartAsync();
        var (_, token) = await oidc.CreateUserTokenAsync(oidc.UniqueEmail("alice"));
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Agents");
        var parent = (await oidc.OkAsync(HttpMethod.Post, $"{Organizations}/{organization}/projects/{project}/api-keys", token,
            new { name = "orchestrator", scopes = Orchestrator }, HttpStatusCode.Created)).GetProperty("key").GetString()!;
        var database = await oidc.CreateDatabaseIdAsync(oidc.UniqueName("agent-1"));

        using var minted = await MintAsync(oidc.Client, parent, new { database, access = "read", ttl_seconds = 600 });
        var body = await minted.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(minted.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(body.ToString());
        await Assert.That(body.GetProperty("project_id").GetString()).IsEqualTo(project);
        await Assert.That(body.GetProperty("expires_at").GetDateTimeOffset()).IsLessThanOrEqualTo(DateTimeOffset.UtcNow.AddSeconds(601));

        using var exchanged = await oidc.ApiKeyTokenAsync(body.GetProperty("key").GetString()!);
        var access = await oidc.ValidateAsync((await OidcHarness.JsonAsync(exchanged)).GetProperty("access_token").GetString()!, Audience);
        await Assert.That(access.GetClaim("project_id").Value).IsEqualTo(project);
        await Assert.That(access.GetClaim("database").Value).IsEqualTo(database);
        await Assert.That(access.GetClaim("scope").Value).IsEqualTo("sql:read");
    }

    [Test]
    public async Task A_scoped_key_mints_nothing_cannot_widen_its_parent_and_dies_with_it()
    {
        await using var oidc = await StartAsync();
        var (_, token) = await oidc.CreateUserTokenAsync(oidc.UniqueEmail("bob"));
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Agents");
        var keys = $"{Organizations}/{organization}/projects/{project}/api-keys";
        var database = await oidc.CreateDatabaseIdAsync(oidc.UniqueName("agent-2"));
        var created = await oidc.OkAsync(HttpMethod.Post, keys, token, new { name = "reader", scopes = ReadOnly, database }, HttpStatusCode.Created);
        var parent = created.GetProperty("key").GetString()!;

        using var widened = await MintAsync(oidc.Client, parent, new { database, access = "read_write" });
        using var elsewhere = await MintAsync(oidc.Client, parent, new { database = await oidc.CreateDatabaseIdAsync(oidc.UniqueName("other")), access = "read" });
        using var minted = await MintAsync(oidc.Client, parent, new { database, access = "read" });
        var child = (await minted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;
        using var grandchild = await MintAsync(oidc.Client, child, new { database, access = "read" });

        await Assert.That(widened.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(elsewhere.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(grandchild.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using (var live = await oidc.ApiKeyTokenAsync(child))
        {
            await Assert.That(live.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        await oidc.OkAsync(HttpMethod.Delete, $"{keys}/{created.GetProperty("api_key").GetProperty("id").GetString()}", token, status: HttpStatusCode.NoContent);
        using var revoked = await oidc.ApiKeyTokenAsync(child);
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    private Task<OidcHarness> StartAsync() => OidcHarness.StartSharedManagementAsync(fixture);

    private static async Task<HttpResponseMessage> MintAsync(HttpClient client, string key, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Mint) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{key[..26]}:{key}")));
        return await client.SendAsync(request);
    }
}
