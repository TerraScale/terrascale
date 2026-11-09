using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Bogus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Webhooks;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class WebhookEndpointTests(TerraBaseFixture fixture)
{
    private readonly Faker faker = new();

    [Test]
    public async Task Admins_register_endpoints_whose_secret_is_answered_once_and_rotates()
    {
        await using var oidc = await OidcHarness.StartSharedManagementAsync(fixture);
        var (_, token) = await oidc.CreateUserTokenAsync(oidc.UniqueEmail("webhook-happy"));
        var organization = await oidc.CreateOrganizationAsync(token, this.faker.Company.CompanyName());
        var project = await oidc.CreateProjectAsync(token, organization, this.faker.Commerce.ProductName());
        var root = $"/v1/{project}";
        var events = new[] { "user.signed_up", "user.disabled" };

        var created = await oidc.OkAsync(HttpMethod.Post, $"{root}/webhook-endpoints", token,
            new { url = "  https://hooks.example.test/lifecycle  ", event_types = events }, HttpStatusCode.Created);
        await Assert.That(created.GetProperty("url").GetString()).IsEqualTo("https://hooks.example.test/lifecycle");
        await Assert.That(created.GetProperty("event_types").EnumerateArray().Select(type => type.GetString()!).ToArray()).IsEquivalentTo(events);
        var id = created.GetProperty("id").GetString()!;
        var secret = created.GetProperty("secret").GetString()!;
        await Assert.That(secret.StartsWith("whsec_", StringComparison.Ordinal)).IsTrue();

        var listed = (await oidc.OkAsync(HttpMethod.Get, $"{root}/webhook-endpoints", token)).GetProperty("items");
        await Assert.That(listed.GetArrayLength()).IsEqualTo(1);
        await Assert.That(listed[0].GetProperty("id").GetString()).IsEqualTo(id);
        await Assert.That(listed[0].TryGetProperty("secret", out _)).IsFalse();
        var stored = await oidc.Database.ReadAsync(db => db.WebhookEndpoints.SingleAsync(endpoint => endpoint.ProjectId == project && endpoint.Id == id));
        await Assert.That(stored.Url).IsEqualTo("https://hooks.example.test/lifecycle");
        await Assert.That(stored.SecretBox).DoesNotContain(secret);
        await Assert.That(stored.SecretBox.StartsWith("v1:", StringComparison.Ordinal)).IsTrue();

        var rotated = await oidc.OkAsync(HttpMethod.Post, $"{root}/webhook-endpoints/{id}/rotate-secret", token);
        var next = rotated.GetProperty("secret").GetString()!;
        await Assert.That(next.StartsWith("whsec_", StringComparison.Ordinal)).IsTrue();
        await Assert.That(next).IsNotEqualTo(secret);
        await Assert.That(rotated.GetProperty("previous_secret_expires_at").GetDateTimeOffset()).IsAfter(DateTimeOffset.UtcNow);

        using var deleted = await oidc.CallAsync(HttpMethod.Delete, $"{root}/webhook-endpoints/{id}", token);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, $"{root}/webhook-endpoints", token)).GetProperty("items").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task Registration_refuses_non_admins_and_admins_without_a_step_up_proof()
    {
        await using var oidc = await OidcHarness.StartSharedManagementAsync(fixture);
        var (_, token) = await oidc.CreateUserTokenAsync(oidc.UniqueEmail("webhook-critical"));
        var organization = await oidc.CreateOrganizationAsync(token, this.faker.Company.CompanyName());
        var project = await oidc.CreateProjectAsync(token, organization, this.faker.Commerce.ProductName());
        var root = $"/v1/{project}";
        var body = new { url = "https://hooks.example.test/lifecycle", event_types = new[] { "user.deleted" } };

        var (viewer, viewerToken) = await oidc.CreateUserTokenAsync(oidc.UniqueEmail("webhook-viewer"));
        await oidc.GrantAsync(project, viewer.Id, Roles.Viewer);
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, $"{root}/webhook-endpoints", viewerToken, body)).IsEqualTo((403, "forbidden_role"));

        var request = new HttpRequestMessage(HttpMethod.Post, $"{root}/webhook-endpoints")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var refused = await oidc.Client.SendAsync(request);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await OidcHarness.EnvelopeCodeAsync(refused)).IsEqualTo("step_up_required");
    }
}
