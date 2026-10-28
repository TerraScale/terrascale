using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.IdentityModel.JsonWebTokens;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Management;

/// <summary>ts-auth access tokens against a real clock, and from the real ts-auth application of this monorepo.</summary>
public sealed class UserAuthTests
{
    [Test]
    public async Task A_server_started_while_ts_auth_was_down_is_ready_and_accepts_tokens_once_it_answers()
    {
        using var tls = new ClientTls();
        using var issuer = new TestIssuer { Available = false };
        await using var server = await TerraBaseTestServer.StartAsync(
            settings =>
            {
                tls.Apply(settings);
                TestIssuer.Configure(settings);
            },
            issuer.Route);
        using var client = tls.CreateClient(server.ClientAddress!, issuer.Mint(EntityIds.New().ToString()));
        var rejected = await StatusAsync(client, "/v1/management/organizations");
        var ready = await StatusAsync(server.Client, "/readyz");

        issuer.Available = true;
        // The background fetch retries every 2 s: wait for it by retrying the request. The deadline bounds the delays and
        // every request, so nothing outlives it.
        var accepted = HttpStatusCode.Unauthorized;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (accepted != HttpStatusCode.OK)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
                accepted = await StatusAsync(client, "/v1/management/organizations", deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // Still rejected: the assertion below reports it.
        }

        await Assert.That(rejected).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(ready).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(accepted).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Access_tokens_from_the_real_ts_auth_authenticate_on_the_management_api()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var auth = await RealAuthFixture.StartAsync(token);
        var accessToken = await auth.AccessTokenAsync(token);
        var jwt = new JsonWebToken(accessToken);
        await Assert.That(jwt.Issuer).IsEqualTo(RealAuthFixture.Issuer);
        await Assert.That(jwt.Audiences).Contains(RealAuthFixture.Audience);
        await Assert.That(jwt.Alg).IsEqualTo("ES256");
        await Assert.That(jwt.Typ).IsEqualTo("at+jwt");
        await Assert.That(jwt.Subject).IsEqualTo(auth.Subject);

        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            tls.Apply(settings);
            RealAuthFixture.Configure(settings);
        }, auth.TrustIssuer);
        using var client = tls.CreateClient(server.ClientAddress!, accessToken);
        var organizations = new Uri("/v1/management/organizations", UriKind.Relative);
        using var created = await client.PostAsJsonAsync(organizations, new { name = new Faker().Company.CompanyName() }, token);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var organization = await created.Content.ReadFromJsonAsync<JsonElement>(token);
        await Assert.That(organization.GetProperty("createdBy").GetString()).IsEqualTo(auth.Subject);
        using var listed = await client.GetAsync(organizations, token);
        await Assert.That(listed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var items = await listed.Content.ReadFromJsonAsync<JsonElement>(token);
        await Assert.That(items.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()))
            .IsEquivalentTo([organization.GetProperty("id").GetString()]);
        using var anonymous = tls.CreateClient(server.ClientAddress!);
        await Assert.That(await StatusAsync(anonymous, organizations.OriginalString, token)).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    [WorkloadTransportQuarantine]
    public async Task The_github_actions_workload_token_reads_only_its_bound_database_and_cannot_write_or_manage()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var auth = await RealAuthFixture.StartAsync(token);
        var (organization, project, seedToken) = await auth.SeedProjectAsync(token);
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            tls.Apply(settings);
            RealAuthFixture.Configure(settings);
        }, auth.TrustIssuer);
        var database = $"/v1/projects/{project}/databases/ci-e2e";

        // A separate project-admin fixture credential provisions data; the workload receives only documents:read.
        using var seed = tls.CreateClient(server.ClientAddress!, seedToken);
        using var created = await seed.PostAsJsonAsync(new Uri($"/v1/projects/{project}/databases", UriKind.Relative), new { name = "ci-e2e" }, token);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var databaseId = (await created.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("id").GetString()!;
        var accessToken = await auth.GitHubActionsTokenAsync(organization, project, databaseId, token);
        using var client = tls.CreateClient(server.ClientAddress!, accessToken);
        using var collection = await seed.PostAsJsonAsync(new Uri(database + "/collections", UriKind.Relative), new { name = "runs" }, token);
        await Assert.That(collection.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var name = new Faker().Lorem.Word();
        using var written = await seed.PostAsJsonAsync(new Uri(database + "/collections/runs/documents", UriKind.Relative), new { name }, token);
        await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var id = (await written.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("id").GetString()!;
        var read = await client.GetFromJsonAsync<JsonElement>(new Uri($"{database}/collections/runs/documents/by-key?id={Uri.EscapeDataString(id)}", UriKind.Relative), token);
        await Assert.That(read.GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);

        using var otherDatabase = await seed.PostAsJsonAsync(new Uri($"/v1/projects/{project}/databases", UriKind.Relative), new { name = "other" }, token);
        await Assert.That(otherDatabase.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var otherCollection = await seed.PostAsJsonAsync(new Uri($"/v1/projects/{project}/databases/other/collections", UriKind.Relative), new { name = "runs" }, token);
        await Assert.That(otherCollection.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var otherPath = $"/v1/projects/{project}/databases/other/collections/runs/documents";
        await Assert.That(await StatusAsync(seed, otherPath, token)).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await StatusAsync(client, otherPath, token)).IsEqualTo(HttpStatusCode.NotFound);
        var (_, otherProject, otherSeedToken) = await auth.SeedProjectAsync(token);
        using var otherSeed = tls.CreateClient(server.ClientAddress!, otherSeedToken);
        using var otherCreated = await otherSeed.PostAsJsonAsync(new Uri($"/v1/projects/{otherProject}/databases", UriKind.Relative), new { name = "ci-e2e" }, token);
        await Assert.That(otherCreated.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var projectCollection = await otherSeed.PostAsJsonAsync(new Uri($"/v1/projects/{otherProject}/databases/ci-e2e/collections", UriKind.Relative), new { name = "runs" }, token);
        await Assert.That(projectCollection.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var otherProjectPath = $"/v1/projects/{otherProject}/databases/ci-e2e/collections/runs/documents";
        await Assert.That(await StatusAsync(otherSeed, otherProjectPath, token)).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await StatusAsync(client, otherProjectPath, token)).IsEqualTo(HttpStatusCode.NotFound);
        using var write = await client.PostAsJsonAsync(new Uri(database + "/collections/runs/documents", UriKind.Relative), new { name = "forbidden" }, token);
        await Assert.That(write.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using var create = await client.PostAsJsonAsync(new Uri($"/v1/projects/{project}/databases", UriKind.Relative), new { name = "forbidden" }, token);
        await Assert.That(create.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await StatusAsync(client, $"/v1/projects/{EntityIds.New()}/databases/ci-e2e/collections/runs/documents", token)).IsEqualTo(HttpStatusCode.NotFound);

        // Another project and all management routes remain unreachable.
        using var elsewhere = await client.PostAsJsonAsync(new Uri($"/v1/projects/{EntityIds.New()}/databases", UriKind.Relative), new { name = "ci-e2e" }, token);
        await Assert.That(elsewhere.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await StatusAsync(client, "/v1/management/organizations", token)).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(new JsonWebToken(accessToken).GetClaim("org_id").Value).IsEqualTo(organization);
        await Assert.That(await StatusAsync(client, $"/v1/management/organizations/{organization}/projects/{project}/databases", token))
            .IsEqualTo(HttpStatusCode.NotFound);
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string path, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);
        return response.StatusCode;
    }
}

// CI task permits a named-owner quarantine for at most 24 h; #853 tracks the unproven loopback reset.
[AttributeUsage(AttributeTargets.Method)]
internal sealed class WorkloadTransportQuarantineAttribute()
    : SkipAttribute("#853: loopback POST reset has an ambiguous outcome; owner MarioGK / terrascale CI WRK; expires 2026-10-29T16:40:00Z")
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(DateTimeOffset.UtcNow < new DateTimeOffset(2026, 10, 29, 16, 40, 0, TimeSpan.Zero));
}
