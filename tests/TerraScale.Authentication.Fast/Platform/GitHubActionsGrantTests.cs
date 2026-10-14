using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Store;
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
        await Assert.That(access.Subject).IsEqualTo(GitHubActionsGrant.ClientId);
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
    [Arguments("job_workflow_ref", "TerraScale/terrascale/.github/workflows/reusable.yml@refs/heads/main")]
    [Arguments("job_workflow_ref", "TerraScale/terrascale/.github/workflows/ci.yml@refs/heads/feature")]
    [Arguments("job_workflow_ref", "")]
    [Arguments("jti", null)]
    [Arguments("run_id", null)]
    [Arguments("repository_id", null)]
    [Arguments("repository_owner_id", null)]
    [Arguments("repository", null)]
    [Arguments("ref", null)]
    [Arguments("event_name", null)]
    [Arguments("workflow_ref", null)]
    [Arguments("repository_id", "")]
    [Arguments("workflow_ref", "terrascale/terrascale/.github/workflows/ci.yml@refs/heads/main")]
    public async Task A_token_outside_the_trust_policy_is_refused(string claim, string? value)
    {
        using var github = new FakeGitHubActions();
        await using var oidc = await OidcHarness.StartAsync(fixture, services: github.Route);
        await GitHubActionsBinding.BindAsync(oidc.Server.App.Services, CancellationToken.None);

        var (_, error) = await ErrorAsync(await RequestAsync(oidc, github.Mint(OidcHarness.Issuer, new Dictionary<string, string?> { [claim] = value })));

        await Assert.That(error).IsEqualTo("invalid_grant");
    }

    [Test]
    [Arguments("null")]
    [Arguments("42")]
    [Arguments("true")]
    [Arguments("{}")]
    [Arguments("[\"TerraScale/terrascale/.github/workflows/ci.yml@refs/heads/main\"]")]
    public async Task A_job_workflow_ref_that_is_not_ci_yml_s_string_is_refused(string json)
    {
        // Present in any non-string JSON form, job_workflow_ref is not "absent": it is refused, never read as a plain workflow.
        using var github = new FakeGitHubActions();
        await using var oidc = await OidcHarness.StartAsync(fixture, services: github.Route);
        await GitHubActionsBinding.BindAsync(oidc.Server.App.Services, CancellationToken.None);
        using var value = JsonDocument.Parse(json);

        var (_, error) = await ErrorAsync(await RequestAsync(oidc, github.Mint(OidcHarness.Issuer,
            json: new Dictionary<string, JsonElement> { ["job_workflow_ref"] = value.RootElement.Clone() })));

        await Assert.That(error).IsEqualTo("invalid_grant");
    }

    [Test]
    public async Task A_job_of_ci_yml_itself_without_job_workflow_ref_gets_the_bound_project_token()
    {
        // GitHub documents job_workflow_ref only for jobs of a reusable workflow; a job of ci.yml itself may carry none.
        using var github = new FakeGitHubActions();
        await using var oidc = await OidcHarness.StartAsync(fixture, services: github.Route);
        var (organization, project, _) = await GitHubActionsBinding.BindAsync(oidc.Server.App.Services, CancellationToken.None);

        using var response = await RequestAsync(oidc, github.Mint(OidcHarness.Issuer, new Dictionary<string, string?> { ["job_workflow_ref"] = null }));
        var access = new JsonWebToken((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString());

        await Assert.That((int)response.StatusCode).IsEqualTo(200);
        await Assert.That(access.GetClaim("project_id").Value).IsEqualTo(project);
        await Assert.That(access.GetClaim("org_id").Value).IsEqualTo(organization);
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

    [Test]
    public async Task Tokens_from_another_issuer_or_without_time_or_audience_bounds_are_refused()
    {
        using var github = new FakeGitHubActions();
        await using var oidc = await OidcHarness.StartAsync(fixture, services: github.Route);
        await GitHubActionsBinding.BindAsync(oidc.Server.App.Services, CancellationToken.None);

        // Each is signed with GitHub's published key, so only the named claim makes it wrong.
        var refused = new Dictionary<string, string>
        {
            ["issuer of another provider"] = github.Mint(OidcHarness.Issuer, issuer: "https://token.actions.githubusercontent.com.evil.example"),
            ["issuer with a path"] = github.Mint(OidcHarness.Issuer, issuer: GitHubActionsGrant.Issuer + "/terrascale"),
            ["no expiry"] = github.Mint(OidcHarness.Issuer, expires: false),
            ["not valid yet"] = github.Mint(OidcHarness.Issuer, notBeforeIn: TimeSpan.FromMinutes(5), expiresIn: TimeSpan.FromMinutes(10)),
            ["issuer with a trailing slash"] = github.Mint(OidcHarness.Issuer, issuer: GitHubActionsGrant.Issuer + "/"),
            // Just beyond the documented 30 s skew, on both edges.
            ["not valid for another 45 s"] = github.Mint(OidcHarness.Issuer, notBeforeIn: TimeSpan.FromSeconds(45)),
            ["expired 45 s ago"] = github.Mint(OidcHarness.Issuer, notBeforeIn: TimeSpan.FromMinutes(-2), expiresIn: TimeSpan.FromSeconds(-45)),
            ["no audience"] = github.Mint(audience: null),
            ["audience with a trailing slash"] = github.Mint(OidcHarness.Issuer + "/"),
        };

        foreach (var (reason, token) in refused)
        {
            await Assert.That((await ErrorAsync(await RequestAsync(oidc, token))).Error).IsEqualTo("invalid_grant").Because(reason);
        }

        // Still within the 30 s clock skew: accepted.
        using var withinSkew = await RequestAsync(oidc, github.Mint(OidcHarness.Issuer, notBeforeIn: TimeSpan.FromSeconds(10)));
        await Assert.That((int)withinSkew.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task Only_rs256_signatures_by_a_published_github_key_are_accepted()
    {
        using var github = new FakeGitHubActions();
        using var foreign = RSA.Create(2048);
        await using var oidc = await OidcHarness.StartAsync(fixture, services: github.Route);
        await GitHubActionsBinding.BindAsync(oidc.Server.App.Services, CancellationToken.None);
        var valid = github.Mint(OidcHarness.Issuer);
        var parts = valid.Split('.');

        var refused = new Dictionary<string, string>
        {
            // GitHub's own key, but RS384 instead of the pinned RS256.
            ["RS384 by the published key"] = github.Mint(OidcHarness.Issuer, signing: github.Signing(SecurityAlgorithms.RsaSha384)),
            // An HMAC under GitHub's kid: the published key is RSA, so no shared secret may stand in for it.
            ["HS256 under the published kid"] = github.Mint(OidcHarness.Issuer,
                signing: new SigningCredentials(new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = github.KeyId }, SecurityAlgorithms.HmacSha256)),
            // Another RSA key that claims GitHub's kid.
            ["foreign key under the published kid"] = github.Mint(OidcHarness.Issuer,
                signing: new SigningCredentials(new RsaSecurityKey(foreign) { KeyId = github.KeyId }, SecurityAlgorithms.RsaSha256)),
            ["unsigned (alg none)"] = $"{Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""")}.{parts[1]}.",
            ["valid header and payload, signature removed"] = $"{parts[0]}.{parts[1]}.",
            ["valid token, payload changed"] = $"{parts[0]}.{Base64UrlEncoder.Encode(Base64UrlEncoder.Decode(parts[1]).Replace("refs/heads/main", "refs/heads/evil", StringComparison.Ordinal))}.{parts[2]}",
        };

        foreach (var (reason, token) in refused)
        {
            await Assert.That((await ErrorAsync(await RequestAsync(oidc, token))).Error).IsEqualTo("invalid_grant").Because(reason);
        }

        using var accepted = await RequestAsync(oidc, valid);
        await Assert.That((int)accepted.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task The_caller_cannot_choose_another_project_or_organization()
    {
        using var github = new FakeGitHubActions();
        await using var oidc = await OidcHarness.StartAsync(fixture, services: github.Route);
        var (organization, project, _) = await GitHubActionsBinding.BindAsync(oidc.Server.App.Services, CancellationToken.None);
        var otherOrganization = AuthStore.NewId();
        var otherProject = AuthStore.NewId();

        using var response = await RequestAsync(oidc, github.Mint(OidcHarness.Issuer), extra: new Dictionary<string, string>
        {
            ["organization"] = otherOrganization,
            ["project"] = otherProject,
        });
        var access = new JsonWebToken((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString());

        // The binding alone names the project: request parameters never widen or move the token.
        await Assert.That(access.GetClaim("project_id").Value).IsEqualTo(project);
        await Assert.That(access.GetClaim("org_id").Value).IsEqualTo(organization);
    }

    private static Task<HttpResponseMessage> RequestAsync(OidcHarness oidc, string subjectToken, string? scope = null, IReadOnlyDictionary<string, string>? extra = null)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = GitHubActionsGrant.GrantType,
            ["client_id"] = GitHubActionsGrant.ClientId,
            ["subject_token"] = subjectToken,
        };
        foreach (var (name, value) in extra ?? new Dictionary<string, string>())
        {
            form[name] = value;
        }

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
