using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Platform;

/// <summary>
/// The GitHub Actions grant against a fake GitHub issuer (no request reaches GitHub): inert until bound; once bound, only a
/// live, GitHub-signed token for the platform issuer that matches every claim of the trust policy, used once, gets a
/// 15-minute token of the bound e2e project with at most the workload's scopes; unbinding ends it.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class GitHubActionsGrantTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task An_unbound_workload_gets_nothing_even_with_a_valid_github_token()
    {
        using var github = new FakeGitHubActions();
        await using var oidc = await OidcHarness.StartAsync(fixture, services: github.Route);

        var (status, error) = await ErrorAsync(await RequestAsync(oidc, github.Mint(OidcHarness.Issuer)));

        await Assert.That(status).IsGreaterThanOrEqualTo(400);
        await Assert.That(error).IsEqualTo("invalid_client");
    }

    [Test]
    public async Task A_bound_workload_gets_a_15_minute_token_of_the_e2e_project_only_and_unbinding_ends_it()
    {
        using var github = new FakeGitHubActions();
        await using var oidc = await OidcHarness.StartAsync(fixture, services: github.Route);
        var (organization, project, created) = await GitHubActionsBinding.BindAsync(oidc.Server.App.Services, CancellationToken.None);
        var again = await GitHubActionsBinding.BindAsync(oidc.Server.App.Services, CancellationToken.None);

        using var response = await RequestAsync(oidc, github.Mint(OidcHarness.Issuer));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var access = new JsonWebToken(body.GetProperty("access_token").GetString());

        await Assert.That(created).IsTrue();
        await Assert.That(again).IsEqualTo((organization, project, false));
        await Assert.That(access.GetClaim("project_id").Value).IsEqualTo(project);
        await Assert.That(access.GetClaim("org_id").Value).IsEqualTo(organization);
        await Assert.That(access.GetClaim("scope").Value.Split(' ')).IsEquivalentTo(GitHubActionsGrant.Scopes);
        await Assert.That(access.Subject).IsEqualTo($"github-actions:{GitHubActionsGrant.RepositoryId}:18000000001");
        await Assert.That(access.ValidTo - access.IssuedAt).IsLessThanOrEqualTo(TimeSpan.FromMinutes(15));
        await Assert.That(body.TryGetProperty("refresh_token", out _)).IsFalse();

        // A narrower request gets only what it asked for. More than the workload's scopes is refused by OpenIddict itself:
        // the client holds no permission for it (invalid_request, before the grant runs).
        using var narrow = await RequestAsync(oidc, github.Mint(OidcHarness.Issuer), "documents:read");
        var narrowed = new JsonWebToken((await narrow.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString());
        await Assert.That(narrowed.GetClaim("scope").Value).IsEqualTo("documents:read");
        await Assert.That((await ErrorAsync(await RequestAsync(oidc, github.Mint(OidcHarness.Issuer), "documents:read billing:read"))).Error).IsEqualTo("invalid_request");

        await Assert.That(await GitHubActionsBinding.UnbindAsync(oidc.Server.App.Services, CancellationToken.None)).IsTrue();
        await Assert.That((await ErrorAsync(await RequestAsync(oidc, github.Mint(OidcHarness.Issuer)))).Error).IsEqualTo("invalid_client");
        await Assert.That(await GitHubActionsBinding.UnbindAsync(oidc.Server.App.Services, CancellationToken.None)).IsFalse();
    }

    [Test]
    [Arguments("repository_id", "1")]
    [Arguments("repository_owner_id", "1")]
    [Arguments("repository", "TerraScale/other")]
    [Arguments("ref", "refs/heads/feature")]
    [Arguments("ref", "refs/pull/1/merge")]
    [Arguments("event_name", "pull_request")]
    [Arguments("event_name", "workflow_dispatch")]
    [Arguments("workflow_ref", "TerraScale/terrascale/.github/workflows/other.yml@refs/heads/main")]
    [Arguments("job_workflow_ref", "Attacker/repo/.github/workflows/reusable.yml@refs/heads/main")]
    [Arguments("jti", null)]
    [Arguments("run_id", null)]
    [Arguments("repository_id", null)]
    public async Task A_token_outside_the_trust_policy_is_refused(string claim, string? value)
    {
        using var github = new FakeGitHubActions();
        await using var oidc = await OidcHarness.StartAsync(fixture, services: github.Route);
        await GitHubActionsBinding.BindAsync(oidc.Server.App.Services, CancellationToken.None);

        var (_, error) = await ErrorAsync(await RequestAsync(oidc, github.Mint(OidcHarness.Issuer, new Dictionary<string, string?> { [claim] = value })));

        await Assert.That(error).IsEqualTo("invalid_grant");
    }

    [Test]
    public async Task Tokens_for_another_audience_expired_unsigned_by_github_or_replayed_are_refused()
    {
        using var github = new FakeGitHubActions();
        using var foreign = RSA.Create(2048);
        await using var oidc = await OidcHarness.StartAsync(fixture, services: github.Route);
        await GitHubActionsBinding.BindAsync(oidc.Server.App.Services, CancellationToken.None);

        var otherAudience = await ErrorAsync(await RequestAsync(oidc, github.Mint("https://api.terrascale.tech")));
        var expired = await ErrorAsync(await RequestAsync(oidc, github.Mint(OidcHarness.Issuer, expiresIn: TimeSpan.FromMinutes(-5))));
        var unsigned = await ErrorAsync(await RequestAsync(oidc, github.Mint(OidcHarness.Issuer,
            signing: new SigningCredentials(new RsaSecurityKey(foreign) { KeyId = "not-github" }, SecurityAlgorithms.RsaSha256))));
        var token = github.Mint(OidcHarness.Issuer);
        using var first = await RequestAsync(oidc, token);
        var replayed = await ErrorAsync(await RequestAsync(oidc, token));

        await Assert.That(otherAudience.Error).IsEqualTo("invalid_grant");
        await Assert.That(expired.Error).IsEqualTo("invalid_grant");
        await Assert.That(unsigned.Error).IsEqualTo("invalid_grant");
        await Assert.That((int)first.StatusCode).IsEqualTo(200);
        await Assert.That(replayed.Error).IsEqualTo("invalid_grant");
    }

    private static Task<HttpResponseMessage> RequestAsync(OidcHarness oidc, string subjectToken, string? scope = null)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = GitHubActionsGrant.GrantType,
            ["client_id"] = GitHubActionsGrant.ClientId,
            ["subject_token"] = subjectToken,
        };
        if (scope is not null)
        {
            form["scope"] = scope;
        }

        return oidc.Client.PostAsync(new Uri("/terrascale/oauth/token", UriKind.Relative), new FormUrlEncodedContent(form));
    }

    private static async Task<(int Status, string? Error)> ErrorAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return ((int)response.StatusCode, body.TryGetProperty("error", out var error) ? error.GetString() : null);
        }
    }
}
