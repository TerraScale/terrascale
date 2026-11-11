using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Tests;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Fast.Management;

/// <summary>
/// API keys and the token grants of the platform contract (TS-598): an API key's client credentials and a user's token
/// exchange, each for an organization or project token, over real HTTP on the real TerraBase.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class ApiKeyTests(TerraBaseFixture fixture)
{
    private const string Audience = "https://api.terrascale.tech";
    private static readonly string[] ReadWrite = ["sql:read", "sql:write"];



    // The dashboard sends ts-auth the scope set below when a user creates a full-access key. Read from the dashboard's
    // own source, so a scope rename that leaves the two products disagreeing at one commit fails here.
    [Test]
    public async Task A_key_with_the_scopes_the_dashboard_sends_is_created_and_its_token_carries_them()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TerraScale.slnx")))
        {
            root = root.Parent;
        }

        var source = await File.ReadAllTextAsync(Path.Combine(root!.FullName, "src", "TerraScale.Dashboard", "Features", "ApiKeys", "ApiKeyService.cs"));
        var scopes = DashboardFullAccess().Match(source).Groups["scopes"].Value.Split(',').Select(scope => scope.Trim().Trim('"')).ToArray();
        await Assert.That(scopes).IsNotEmpty();

        await using var oidc = await StartAsync();
        var (_, token) = await oidc.CreateUserTokenAsync(oidc.UniqueEmail("dana"));
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Production");
        var created = await oidc.OkAsync(HttpMethod.Post, $"{Organizations}/{organization}/projects/{project}/api-keys", token,
            new { name = "dashboard", scopes }, HttpStatusCode.Created);
        using var exchanged = await oidc.ApiKeyTokenAsync(created.GetProperty("key").GetString()!);
        await Assert.That(exchanged.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var access = await oidc.ValidateAsync((await OidcHarness.JsonAsync(exchanged)).GetProperty("access_token").GetString()!, Audience);
        await Assert.That(access.GetClaim("scope").Value.Split(' ')).IsEquivalentTo(scopes);
    }

    [Test]
    public async Task A_project_key_is_created_without_step_up_rotates_only_with_one_and_dies_when_its_creator_signs_out_everywhere()
    {
        await using var oidc = await StartAsync();
        var (_, token) = await oidc.CreateUserTokenAsync(oidc.UniqueEmail("erin"));
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Production");
        var keys = $"{Organizations}/{organization}/projects/{project}/api-keys";

        using var create = new HttpRequestMessage(HttpMethod.Post, keys) { Content = JsonContent.Create(new { name = "no step-up", scopes = ReadWrite }) };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var created = await oidc.Client.SendAsync(create);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var body = await OidcHarness.JsonAsync(created);
        var key = body.GetProperty("key").GetString()!;

        using var rotate = new HttpRequestMessage(HttpMethod.Post, $"{keys}/{body.GetProperty("api_key").GetProperty("id").GetString()}/rotate");
        rotate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var refused = await oidc.Client.SendAsync(rotate);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await OidcHarness.EnvelopeCodeAsync(refused)).IsEqualTo("step_up_required");

        await Assert.That((await TokenErrorAsync(oidc.ApiKeyTokenAsync(key))).Status).IsEqualTo(200);
        await oidc.OkAsync(HttpMethod.Post, "/v1/terrascale/account/sign-out", token, status: HttpStatusCode.NoContent);
        await Assert.That(await TokenErrorAsync(oidc.ApiKeyTokenAsync(key))).IsEqualTo((401, "invalid_client"));
    }

    [Test]
    public async Task The_21st_key_a_user_creates_within_the_hour_answers_429_with_retry_after(CancellationToken cancellationToken)
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (user, token) = await oidc.CreateUserTokenAsync(oidc.UniqueEmail("frank"));
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Production");
        var keys = $"{Organizations}/{organization}/projects/{project}/api-keys";

        var limiter = oidc.Server.App.Services.GetRequiredService<AttemptLimiter>();
        for (var seeded = 1; seeded < ApiKeys.CreationsPerUserPerHour; seeded++)
        {
            await Assert.That(await limiter.AcquireAsync(PlatformDirectory.Instance, "api-key-create", user.Id, ApiKeys.CreationsPerUserPerHour, TimeSpan.FromHours(1), cancellationToken)).IsNull();
        }

        using var last = await CreateAsync("the 20th");
        await Assert.That(last.StatusCode).IsEqualTo(HttpStatusCode.Created);

        using var limited = await CreateAsync("one too many");
        await Assert.That(limited.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(await OidcHarness.EnvelopeCodeAsync(limited)).IsEqualTo("rate_limited");
        await Assert.That(limited.Headers.RetryAfter?.Delta).IsEqualTo(TimeSpan.FromSeconds(3600 - clock.GetUtcNow().ToUnixTimeSeconds() % 3600));

        async Task<HttpResponseMessage> CreateAsync(string name)
        {
            using var create = new HttpRequestMessage(HttpMethod.Post, keys) { Content = JsonContent.Create(new { name, scopes = ReadWrite }) };
            create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await oidc.Client.SendAsync(create, cancellationToken);
        }
    }

    private Task<OidcHarness> StartAsync() => OidcHarness.StartSharedManagementAsync(fixture);

    private static async Task<(int Status, string? Error)> TokenErrorAsync(Task<HttpResponseMessage> call)
    {
        using var response = await call;
        var body = await OidcHarness.JsonAsync(response);
        return ((int)response.StatusCode, body.TryGetProperty("error", out var error) ? error.GetString() : null);
    }

    [GeneratedRegex(@"FullAccessScopes = \[(?<scopes>[^\]]+)\]")]
    private static partial Regex DashboardFullAccess();

    [GeneratedRegex("^[0-7][0-9A-HJKMNP-TV-Z]{25}_[0-9a-hjkmnp-tv-z]{52}$")]
    private static partial Regex KeyString();
}
