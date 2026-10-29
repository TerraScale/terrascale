using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Platform;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class GitHubActionsGrantTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task A_bound_workload_gets_a_15_minute_token_of_the_e2e_project_only_and_unbinding_ends_it()
    {
        using var github = new FakeGitHubActions();
        await using var oidc = await OidcHarness.StartAsync(fixture, services: github.Route);
        var deadline = Deadline(oidc);
        var (organization, project, database, created) = await BindAsync(oidc, deadline);
        var again = await GitHubActionsBinding.BindAsync(oidc.Server.App.Services, organization, project, database, deadline, CancellationToken.None);

        using var response = await RequestAsync(oidc, github.Mint(GitHubActionsGrant.Audience));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var access = new JsonWebToken(body.GetProperty("access_token").GetString());

        await Assert.That(created).IsTrue();
        await Assert.That(again).IsEqualTo((organization, project, false));
        await Assert.That(access.GetClaim("project_id").Value).IsEqualTo(project);
        await Assert.That(access.GetClaim("org_id").Value).IsEqualTo(organization);
        await Assert.That(access.GetClaim("scope").Value.Split(' ')).IsEquivalentTo(GitHubActionsGrant.Scopes);
        await Assert.That(access.GetClaim("database").Value).IsEqualTo(database);
        await Assert.That(access.Audiences).IsEquivalentTo([GitHubActionsGrant.ApiAudience]);
        await Assert.That(body.TryGetProperty("id_token", out _)).IsFalse();
        await Assert.That(access.Subject).IsEqualTo(GitHubActionsGrant.ClientId);
        await Assert.That(access.ValidTo - access.IssuedAt).IsLessThanOrEqualTo(TimeSpan.FromMinutes(5));
        await Assert.That(body.TryGetProperty("refresh_token", out _)).IsFalse();

        // A narrower request gets only what it asked for. More than the workload's scopes is refused by OpenIddict itself:
        // the client holds no permission for it (invalid_request, before the grant runs).
        using var narrow = await RequestAsync(oidc, github.Mint(GitHubActionsGrant.Audience), "documents:read");
        var narrowed = new JsonWebToken((await narrow.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString());
        await Assert.That(narrowed.GetClaim("scope").Value).IsEqualTo("documents:read");
        await Assert.That((await ErrorAsync(await RequestAsync(oidc, github.Mint(GitHubActionsGrant.Audience), "documents:read billing:read"))).Error).IsEqualTo("invalid_request");

        await Assert.That(await GitHubActionsBinding.UnbindAsync(oidc.Server.App.Services, CancellationToken.None)).IsTrue();
        await Assert.That((await ErrorAsync(await RequestAsync(oidc, github.Mint(GitHubActionsGrant.Audience)))).Error).IsEqualTo("invalid_client");
        await Assert.That(await GitHubActionsBinding.UnbindAsync(oidc.Server.App.Services, CancellationToken.None)).IsFalse();
    }


    private static DateTimeOffset Deadline(OidcHarness oidc) =>
        DateTimeOffset.FromUnixTimeSeconds(oidc.Server.App.Services.GetRequiredService<TimeProvider>().GetUtcNow().AddHours(1).ToUnixTimeSeconds());

    private static async Task<(string Organization, string Project, string Database, bool Created)> BindAsync(OidcHarness oidc, DateTimeOffset? deadline = null)
    {
        var user = await oidc.CreateUserAsync();
        var directory = oidc.Server.App.Services.GetRequiredService<PlatformDirectory>();
        var organization = await directory.CreateOrganizationAsync(user, "Isolated E2E", CancellationToken.None);
        var project = await directory.CreateProjectAsync(user.Id, organization.Id, "E2E", CancellationToken.None);
        var database = await oidc.CreateDatabaseIdAsync("ci-e2e");
        var binding = await GitHubActionsBinding.BindAsync(oidc.Server.App.Services, organization.Id, project.Id, database, deadline ?? Deadline(oidc), CancellationToken.None);
        return (binding.Organization, binding.Project, database, binding.Created);
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
