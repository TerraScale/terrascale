using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Slow.Platform;

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

        var (status, error) = await ErrorAsync(await RequestAsync(oidc, github.Mint(GitHubActionsGrant.Audience)));

        await Assert.That(status).IsGreaterThanOrEqualTo(400);
        await Assert.That(error).IsEqualTo("invalid_client");
    }

    [Test]
    public async Task A_token_outside_the_trust_policy_is_refused()
    {
        using var github = new FakeGitHubActions();
        await using var oidc = await OidcHarness.StartAsync(fixture, services: github.Route);
        await BindAsync(oidc);

        foreach (var (claim, value) in new (string Claim, string? Value)[]
        {
            ("sub", null),
            ("sub", ""),
            ("sub", "repo:TerraScale/terrascale:environment:production"),
            ("repository_id", "1"),
            ("repository_owner_id", "1"),
            ("repository", "TerraScale/other"),
            ("ref", "refs/heads/feature"),
            ("ref", "refs/pull/1/merge"),
            ("event_name", "pull_request"),
            ("event_name", "push"),
            ("workflow_ref", "TerraScale/terrascale/.github/workflows/other.yml@refs/heads/main"),
            ("job_workflow_ref", "Attacker/repo/.github/workflows/reusable.yml@refs/heads/main"),
            ("job_workflow_ref", "TerraScale/terrascale/.github/workflows/reusable.yml@refs/heads/main"),
            ("job_workflow_ref", "TerraScale/terrascale/.github/workflows/ci.yml@refs/heads/feature"),
            ("job_workflow_ref", ""),
            ("job_workflow_ref", GitHubActionsGrant.WorkflowRef),
            ("environment", null),
            ("environment", ""),
            ("environment", "staging"),
            ("sub", "repo:TerraScale/ts-orchestrator:ref:refs/heads/main"),
            ("sub", "repo:TerraScale/ts-orchestrator:environment:Production"),
            ("repository_id", "1414527764"),
            ("repository", "terrascale/ts-orchestrator"),
            ("workflow_ref", "TerraScale/ts-orchestrator/.github/workflows/e2e.yml@refs/heads/feature"),
            ("event_name", "pull_request_target"),
            ("ref", "refs/tags/main"),
            ("jti", null),
            ("run_id", null),
            ("run_id", ""),
            ("jti", ""),
            ("repository_id", null),
            ("repository_owner_id", null),
            ("repository", null),
            ("ref", null),
            ("event_name", null),
            ("workflow_ref", null),
            ("repository_id", ""),
            ("workflow_ref", "terrascale/terrascale/.github/workflows/ci.yml@refs/heads/main"),
        })
        {
            var (_, error) = await ErrorAsync(await RequestAsync(oidc, github.Mint(GitHubActionsGrant.Audience, new Dictionary<string, string?> { [claim] = value })));

            await Assert.That(error).IsEqualTo("invalid_grant").Because(claim + " must match the trust policy");
        }
    }

    [Test]
    public async Task A_present_job_workflow_ref_is_refused_in_every_json_form()
    {
        // Present in any non-string JSON form, job_workflow_ref is not "absent": it is refused, never read as a plain workflow.
        using var github = new FakeGitHubActions();
        await using var oidc = await OidcHarness.StartAsync(fixture, services: github.Route);
        await BindAsync(oidc);
        foreach (var json in new[]
        {
            "null",
            "42",
            "true",
            "{}",
            "[\"TerraScale/terrascale/.github/workflows/ci.yml@refs/heads/main\"]",
        })
        {
            using var value = JsonDocument.Parse(json);

            var (_, error) = await ErrorAsync(await RequestAsync(oidc, github.Mint(GitHubActionsGrant.Audience,
                json: new Dictionary<string, JsonElement> { ["job_workflow_ref"] = value.RootElement.Clone() })));

            await Assert.That(error).IsEqualTo("invalid_grant").Because("job_workflow_ref must reject " + json);
        }
    }

    [Test]
    public async Task Required_claims_must_be_scalar_strings()
    {
        using var github = new FakeGitHubActions();
        await using var oidc = await OidcHarness.StartAsync(fixture, services: github.Route);
        await BindAsync(oidc);
        foreach (var claim in new[]
        {
            "iss",
            "aud",
            "sub",
            "environment",
            "repository_id",
            "repository_owner_id",
            "repository",
            "ref",
            "event_name",
            "workflow_ref",
            "jti",
            "run_id",
        })
        {
            var valid = new JsonWebToken(github.Mint(GitHubActionsGrant.Audience));
            var expected = valid.GetClaim(claim).Value;
            var invalid = new List<JsonElement>
            {
                JsonSerializer.SerializeToElement(new[] { expected }),
                JsonSerializer.SerializeToElement<object?>(null),
                JsonSerializer.SerializeToElement(true),
            };
            if (long.TryParse(expected, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                invalid.Add(JsonSerializer.SerializeToElement(number));
            }

            foreach (var value in invalid)
            {
                var (_, error) = await ErrorAsync(await RequestAsync(oidc, github.Mint(GitHubActionsGrant.Audience,
                    json: new Dictionary<string, JsonElement> { [claim] = value })));
                await Assert.That(error).IsEqualTo("invalid_grant").Because(claim + " must be a scalar string");
            }
        }
    }

    [Test]
    public async Task A_direct_e2e_job_without_job_workflow_ref_gets_the_bound_project_token()
    {
        // GitHub documents job_workflow_ref only for jobs of a reusable workflow; a job of e2e.yml itself may carry none.
        using var github = new FakeGitHubActions();
        await using var oidc = await OidcHarness.StartAsync(fixture, services: github.Route);
        var (organization, project, _, _) = await BindAsync(oidc);

        using var response = await RequestAsync(oidc, github.Mint(GitHubActionsGrant.Audience, new Dictionary<string, string?> { ["job_workflow_ref"] = null }));
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
        await BindAsync(oidc);

        var otherAudience = await ErrorAsync(await RequestAsync(oidc, github.Mint("https://api.terrascale.tech")));
        var expired = await ErrorAsync(await RequestAsync(oidc, github.Mint(GitHubActionsGrant.Audience, expiresIn: TimeSpan.FromMinutes(-5))));
        var unsigned = await ErrorAsync(await RequestAsync(oidc, github.Mint(GitHubActionsGrant.Audience,
            signing: new SigningCredentials(new RsaSecurityKey(foreign) { KeyId = "not-github" }, SecurityAlgorithms.RsaSha256))));
        var token = github.Mint(GitHubActionsGrant.Audience);
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
        await BindAsync(oidc);

        // Each is signed with GitHub's published key, so only the named claim makes it wrong.
        var refused = new Dictionary<string, string>
        {
            ["no issuer"] = github.Mint(GitHubActionsGrant.Audience, issuer: null),
            ["issuer of another provider"] = github.Mint(GitHubActionsGrant.Audience, issuer: "https://token.actions.githubusercontent.com.evil.example"),
            ["issuer with a path"] = github.Mint(GitHubActionsGrant.Audience, issuer: GitHubActionsGrant.Issuer + "/terrascale"),
            ["no expiry"] = github.Mint(GitHubActionsGrant.Audience, expires: false),
            ["not valid yet"] = github.Mint(GitHubActionsGrant.Audience, notBeforeIn: TimeSpan.FromMinutes(5), expiresIn: TimeSpan.FromMinutes(10)),
            ["issuer with a trailing slash"] = github.Mint(GitHubActionsGrant.Audience, issuer: GitHubActionsGrant.Issuer + "/"),
            // Just beyond the documented 30 s skew, on both edges.
            ["not valid for another 45 s"] = github.Mint(GitHubActionsGrant.Audience, notBeforeIn: TimeSpan.FromSeconds(45)),
            ["expired 45 s ago"] = github.Mint(GitHubActionsGrant.Audience, notBeforeIn: TimeSpan.FromMinutes(-2), expiresIn: TimeSpan.FromSeconds(-45)),
            ["no audience"] = github.Mint(audience: null),
            ["audience with a trailing slash"] = github.Mint(GitHubActionsGrant.Audience + "/"),
        };

        foreach (var (reason, token) in refused)
        {
            await Assert.That((await ErrorAsync(await RequestAsync(oidc, token))).Error).IsEqualTo("invalid_grant").Because(reason);
        }

        // Still within the 30 s clock skew: accepted.
        using var withinSkew = await RequestAsync(oidc, github.Mint(GitHubActionsGrant.Audience, notBeforeIn: TimeSpan.FromSeconds(10)));
        await Assert.That((int)withinSkew.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task Only_rs256_signatures_by_a_published_github_key_are_accepted()
    {
        using var github = new FakeGitHubActions();
        using var foreign = RSA.Create(2048);
        await using var oidc = await OidcHarness.StartAsync(fixture, services: github.Route);
        await BindAsync(oidc);
        var valid = github.Mint(GitHubActionsGrant.Audience);
        var parts = valid.Split('.');

        var refused = new Dictionary<string, string>
        {
            // GitHub's own key, but RS384 instead of the pinned RS256.
            ["RS384 by the published key"] = github.Mint(GitHubActionsGrant.Audience, signing: github.Signing(SecurityAlgorithms.RsaSha384)),
            // An HMAC under GitHub's kid: the published key is RSA, so no shared secret may stand in for it.
            ["HS256 under the published kid"] = github.Mint(GitHubActionsGrant.Audience,
                signing: new SigningCredentials(new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = github.KeyId }, SecurityAlgorithms.HmacSha256)),
            // Another RSA key that claims GitHub's kid.
            ["foreign key under the published kid"] = github.Mint(GitHubActionsGrant.Audience,
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
        var (organization, project, database, _) = await BindAsync(oidc);
        var otherOrganization = AuthStore.NewId();
        var otherProject = AuthStore.NewId();

        using var response = await RequestAsync(oidc, github.Mint(GitHubActionsGrant.Audience, new Dictionary<string, string?>
        {
            ["org_id"] = otherOrganization,
            ["project_id"] = otherProject,
            ["role"] = "admin",
            ["scope"] = "api-keys:write",
            ["database"] = AuthStore.NewId(),
        }));
        var access = new JsonWebToken((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString());

        // Signed GitHub claims cannot choose a target or add administrative capabilities.
        await Assert.That(access.GetClaim("project_id").Value).IsEqualTo(project);
        await Assert.That(access.GetClaim("org_id").Value).IsEqualTo(organization);
        await Assert.That(access.GetClaim("scope").Value.Split(' ')).IsEquivalentTo(GitHubActionsGrant.Scopes);
        await Assert.That(access.TryGetClaim("role", out _)).IsFalse();
        await Assert.That(access.GetClaim("database").Value).IsEqualTo(database);
    }

    [Test]
    public async Task Explicit_mismatched_targets_are_refused()
    {
        using var github = new FakeGitHubActions();
        await using var oidc = await OidcHarness.StartAsync(fixture, services: github.Route);
        var (organization, project, database, _) = await BindAsync(oidc);
        foreach (var parameter in new[]
        {
            "organization",
            "project",
            "database",
        })
        {
            var assertion = github.Mint(GitHubActionsGrant.Audience);
            var bad = await ErrorAsync(await RequestAsync(oidc, assertion, extra: new Dictionary<string, string> { [parameter] = AuthStore.NewId() }));
            await Assert.That(bad.Error).IsEqualTo("invalid_target");
            using var good = await RequestAsync(oidc, assertion, "documents:read", new Dictionary<string, string>
            {
                ["organization"] = organization,
                ["project"] = project,
                ["database"] = database,
            });
            await Assert.That((int)good.StatusCode).IsEqualTo(200);
        }
    }

    [Test]
    public async Task Every_scope_outside_documents_read_is_refused()
    {
        using var github = new FakeGitHubActions();
        await using var oidc = await OidcHarness.StartAsync(fixture, services: github.Route);
        await BindAsync(oidc);
        foreach (var scope in new[]
        {
            "documents:write",
            "databases:read",
            "databases:write",
            "api-keys:write",
            "openid",
            "offline_access",
        })
        {
            var result = await ErrorAsync(await RequestAsync(oidc, github.Mint(GitHubActionsGrant.Audience), scope));
            await Assert.That(result.Status).IsGreaterThanOrEqualTo(400);
            await Assert.That(result.Error is "invalid_request" or "invalid_scope").IsTrue();
        }
    }

    [Test]
    public async Task Missing_or_malformed_binding_properties_fail_closed()
    {
        using var github = new FakeGitHubActions();
        await using var oidc = await OidcHarness.StartAsync(fixture, services: github.Route);
        await BindAsync(oidc);
        await using var scope = oidc.Server.App.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var application = (await manager.FindByClientIdAsync(GitHubActionsGrant.ClientId))!;
        var original = await manager.GetPropertiesAsync(application);
        foreach (var property in new[]
        {
            GitHubActionsGrant.OrganizationProperty,
            GitHubActionsGrant.ProjectProperty,
            GitHubActionsGrant.DatabaseProperty,
            GitHubActionsGrant.ActivatedProperty,
            GitHubActionsGrant.ExpiresProperty,
        })
        {
            foreach (var value in new[] { "null", "42", "true", "{}", "[]", "\"\"", "\"/invalid\"" })
            {
                var properties = original.ToBuilder();
                properties[property] = JsonSerializer.Deserialize<JsonElement>(value);
                await UpdateBindingAsync(manager, application, properties.ToImmutable());
                await Assert.That((await ErrorAsync(await RequestAsync(oidc, github.Mint(GitHubActionsGrant.Audience)))).Error).IsEqualTo("invalid_client");
            }

            await UpdateBindingAsync(manager, application, original.Remove(property));
            await Assert.That((await ErrorAsync(await RequestAsync(oidc, github.Mint(GitHubActionsGrant.Audience)))).Error).IsEqualTo("invalid_client");
        }
    }

    [Test]
    public async Task Binding_deadline_caps_tokens_and_denies_at_the_exact_boundary()
    {
        using var github = new FakeGitHubActions();
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var clock = new FakeTimeProvider(now);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock, services: github.Route);
        var deadline = now.AddMinutes(10);
        await BindAsync(oidc, deadline);
        clock.Advance(TimeSpan.FromMinutes(9));
        using var response = await RequestAsync(oidc, github.Mint(GitHubActionsGrant.Audience));
        var access = new JsonWebToken((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString());
        await Assert.That(new DateTimeOffset(access.ValidTo, TimeSpan.Zero)).IsEqualTo(deadline);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.That((await ErrorAsync(await RequestAsync(oidc, github.Mint(GitHubActionsGrant.Audience)))).Error).IsEqualTo("invalid_client");
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That((await ErrorAsync(await RequestAsync(oidc, github.Mint(GitHubActionsGrant.Audience)))).Error).IsEqualTo("invalid_client");
    }

    [Test]
    public async Task Binding_requires_existing_targets_a_UTC_deadline_within_24_hours_and_no_retargeting()
    {
        using var github = new FakeGitHubActions();
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var clock = new FakeTimeProvider(now);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock, services: github.Route);
        var services = oidc.Server.App.Services;
        var deadline = now.AddHours(24);
        var (organization, project, database, _) = await BindAsync(oidc, deadline);
        foreach (var invalid in new[] { now, now.AddSeconds(-1), deadline.AddSeconds(1), deadline.ToOffset(TimeSpan.FromHours(1)), deadline.AddTicks(1) })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => GitHubActionsBinding.BindAsync(services, organization, project, database, invalid, CancellationToken.None));
        }

        await Assert.ThrowsAsync<ArgumentException>(() => GitHubActionsBinding.BindAsync(services, "bad", project, database, deadline, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => GitHubActionsBinding.BindAsync(services, organization, project, "bad/name", deadline, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => GitHubActionsBinding.BindAsync(services, organization, project, "ci-e2e", deadline, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitHubActionsBinding.BindAsync(services, AuthStore.NewId(), project, database, deadline, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitHubActionsBinding.BindAsync(services, organization, AuthStore.NewId(), database, deadline, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitHubActionsBinding.BindAsync(services, organization, project, AuthStore.NewId(), deadline, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitHubActionsBinding.BindAsync(services, organization, project, database, deadline.AddSeconds(-1), CancellationToken.None));
        using var accepted = await RequestAsync(oidc, github.Mint(GitHubActionsGrant.Audience));
        await Assert.That((int)accepted.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task One_assertion_wins_a_race_across_replicas()
    {
        using var github = new FakeGitHubActions();
        await using var oidc = await OidcHarness.StartAsync(fixture, services: github.Route);
        await BindAsync(oidc);
        await using var replica = await oidc.StartReplicaAsync(services: github.Route);
        var assertion = github.Mint(GitHubActionsGrant.Audience);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = GitHubActionsGrant.GrantType,
            ["client_id"] = GitHubActionsGrant.ClientId,
            ["subject_token"] = assertion,
        };
        var requests = new[]
        {
            RequestAsync(oidc, assertion),
            replica.Client.PostAsync(new Uri("/terrascale/oauth/token", UriKind.Relative), new FormUrlEncodedContent(form)),
        };
        var responses = await Task.WhenAll(requests);
        var results = await Task.WhenAll(responses.Select(ErrorAsync));
        await Assert.That(results.Count(result => result.Status == 200)).IsEqualTo(1);
        await Assert.That(results.Count(result => result.Error == "invalid_grant")).IsEqualTo(1);
    }

    [Test]
    public async Task Semantically_invalid_binding_metadata_and_legacy_permissions_issue_nothing()
    {
        using var github = new FakeGitHubActions();
        await using var oidc = await OidcHarness.StartAsync(fixture, services: github.Route);
        var deadline = Deadline(oidc);
        var (organization, project, database, _) = await BindAsync(oidc, deadline);
        await using var scope = oidc.Server.App.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var application = (await manager.FindByClientIdAsync(GitHubActionsGrant.ClientId))!;
        var original = await manager.GetPropertiesAsync(application);
        var now = oidc.Server.App.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        var directory = oidc.Server.App.Services.GetRequiredService<PlatformDirectory>();
        var owner = (await directory.ProjectAsync(project, CancellationToken.None))!.Value.CreatedBy;
        var user = (await oidc.Server.App.Services.GetRequiredService<TerraScale.Authentication.Features.Identity.Users>().GetAsync(PlatformDirectory.Instance, owner))!;
        var otherOrganization = await directory.CreateOrganizationAsync(user, "Other", CancellationToken.None);
        foreach (var (property, value) in new[]
        {
            (GitHubActionsGrant.ActivatedProperty, now.AddMinutes(5).ToString(GitHubActionsGrant.TimestampFormat, CultureInfo.InvariantCulture)),
            (GitHubActionsGrant.ActivatedProperty, deadline.ToString(GitHubActionsGrant.TimestampFormat, CultureInfo.InvariantCulture)),
            (GitHubActionsGrant.ExpiresProperty, now.AddSeconds(-1).ToString(GitHubActionsGrant.TimestampFormat, CultureInfo.InvariantCulture)),
            (GitHubActionsGrant.ExpiresProperty, now.AddHours(25).ToString(GitHubActionsGrant.TimestampFormat, CultureInfo.InvariantCulture)),
            (GitHubActionsGrant.ExpiresProperty, deadline.ToString("O", CultureInfo.InvariantCulture)),
            (GitHubActionsGrant.OrganizationProperty, otherOrganization.Id),
            (GitHubActionsGrant.ProjectProperty, AuthStore.NewId()),
        })
        {
            await UpdateBindingAsync(manager, application, original.SetItem(property, JsonSerializer.SerializeToElement(value)));
            await Assert.That((await ErrorAsync(await RequestAsync(oidc, github.Mint(GitHubActionsGrant.Audience)))).Error).IsEqualTo("invalid_client");
            await Assert.ThrowsAsync<InvalidOperationException>(() => GitHubActionsBinding.BindAsync(oidc.Server.App.Services, organization, project, database, deadline, CancellationToken.None));
        }

        await UpdateBindingAsync(manager, application, original);
        foreach (var permission in new[] { "", OpenIddictConstants.Permissions.Prefixes.Scope + TerraBaseScopes.DocumentsWrite })
        {
            var descriptor = new OpenIddictApplicationDescriptor();
            await manager.PopulateAsync(descriptor, application);
            descriptor.Permissions.Remove(OpenIddictConstants.Permissions.Prefixes.Scope + TerraBaseScopes.DocumentsRead);
            if (permission.Length > 0)
            {
                descriptor.Permissions.Add(permission);
            }

            await manager.UpdateAsync(application, descriptor);
            await Assert.That((await ErrorAsync(await RequestAsync(oidc, github.Mint(GitHubActionsGrant.Audience)))).Error).IsEqualTo("invalid_client");
        }
    }

    [Test]
    public async Task The_explicit_command_contract_is_idempotent_and_deleted_projects_stop_issuance()
    {
        using var github = new FakeGitHubActions();
        await using var oidc = await OidcHarness.StartAsync(fixture, runClosureWorker: false, services: github.Route);
        var services = oidc.Server.App.Services;
        var deadline = Deadline(oidc);
        var (organization, project, database, _) = await BindAsync(oidc, deadline);
        var args = new[] { GitHubActionsBinding.BindCommand, organization, project, database, deadline.ToString(GitHubActionsGrant.TimestampFormat, CultureInfo.InvariantCulture) };
        await Assert.That(await GitHubActionsBinding.RunAsync(args, services, CancellationToken.None)).IsEqualTo(0);
        await Assert.That(await GitHubActionsBinding.RunAsync([GitHubActionsBinding.BindCommand], services, CancellationToken.None)).IsEqualTo(1);
        args[4] = deadline.ToString("O", CultureInfo.InvariantCulture);
        await Assert.That(await GitHubActionsBinding.RunAsync(args, services, CancellationToken.None)).IsEqualTo(1);
        await Assert.That((await ErrorAsync(await RequestAsync(oidc, ""))).Error).IsEqualTo("invalid_request");
        var directory = services.GetRequiredService<PlatformDirectory>();
        var record = (await directory.ProjectAsync(project, CancellationToken.None))!.Value;
        await Assert.That(await services.GetRequiredService<Closures>().DeleteProjectAsync(record.CreatedBy, organization, project, CancellationToken.None)).IsTrue();
        await Assert.That((await ErrorAsync(await RequestAsync(oidc, github.Mint(GitHubActionsGrant.Audience)))).Error).IsEqualTo("invalid_client");
    }

    private static async Task UpdateBindingAsync(IOpenIddictApplicationManager manager, object application, IReadOnlyDictionary<string, JsonElement> properties)
    {
        var descriptor = new OpenIddictApplicationDescriptor();
        await manager.PopulateAsync(descriptor, application);
        descriptor.Properties.Clear();
        foreach (var (key, value) in properties)
        {
            descriptor.Properties[key] = value;
        }

        await manager.UpdateAsync(application, descriptor);
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
