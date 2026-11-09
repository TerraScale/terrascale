using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Webhooks;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class WebhookOutboxTests(TerraBaseFixture fixture)
{
    private readonly Faker faker = new();

    private static readonly string[] SignedUp = ["user.signed_up"];

    [Test]
    public async Task A_committed_project_sign_up_writes_exactly_one_pending_event()
    {
        await using var oidc = await OidcHarness.StartSharedManagementAsync(fixture);
        var (_, token) = await oidc.CreateUserTokenAsync(oidc.UniqueEmail("outbox-happy"));
        var organization = await oidc.CreateOrganizationAsync(token, this.faker.Company.CompanyName());
        var project = await oidc.CreateProjectAsync(token, organization, this.faker.Commerce.ProductName());
        await oidc.OkAsync(HttpMethod.Post, $"/v1/{project}/webhook-endpoints", token,
            new { url = "https://hooks.example.test/lifecycle", event_types = SignedUp }, HttpStatusCode.Created);

        var email = oidc.UniqueEmail("outbox-end-user");
        using var started = await oidc.PostFormAsync($"/ui/{project}/sign-up", new() { ["email"] = email, ["password"] = "A sound body keeps a sound mind!" });
        var pending = PendingToken().Match(await started.Content.ReadAsStringAsync()).Groups[1].Value;
        var code = await oidc.Email.NextCodeAsync(email);
        using var verified = await oidc.PostFormAsync($"/ui/{project}/sign-up/verify", new() { ["email"] = email, ["code"] = code, ["pending_token"] = pending, ["next"] = string.Empty });
        await Assert.That(verified.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        List<JsonElement> events = [];
        await foreach (var row in store.QueryAsync(AuthStore.WebhookOutboxTable, project, "/instanceId", project))
        {
            events.Add(row.Body);
        }

        await Assert.That(events).HasSingleItem();
        var delivered = events[0];
        await Assert.That(delivered.GetProperty("eventType").GetString()).IsEqualTo("user.signed_up");
        await Assert.That(delivered.GetProperty("status").GetString()).IsEqualTo("pending");
        await Assert.That(delivered.GetProperty("attempts").GetInt32()).IsEqualTo(0);
        await Assert.That(delivered.GetProperty("payload").GetProperty("email").GetString()).IsEqualTo(email);
        await Assert.That(delivered.GetProperty("nextAttemptAt").GetDateTimeOffset()).IsGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [GeneratedRegex("name=\"pending_token\" value=\"([^\"]+)\"")]
    private static partial Regex PendingToken();
}
