using System.Net;
using TerraScale.Dashboard.Common.Accounts;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.E2E.Features.Organizations;

// Ownership transfer start goes through hosted step-up: without a fresh confirmation the form only
// starts verification, and the approved resume performs the mutation.
public sealed class OwnershipTransferStepUpJourneyTests
{
    private const string UserId = "test-user";

    [Test]
    public async Task OwnershipTransferStartNeedsAFreshConfirmation()
    {
        await using var scenario = await Scenario.StartAsync();
        var organization = scenario.AddOrganization("Transfer Co", owner: UserId, member: "other-user");
        var path = "/api/v1/dashboard/organization/members/ownership?organizationId=" + organization;
        var form = new Dictionary<string, string> { ["_csrf"] = await HttpJourney.CsrfAsync(scenario.Client, "/organization?tab=members"), ["userID"] = "other-user" };

        using (var unconfirmed = await scenario.PostAsync(path, form))
        {
            await Assert.That(unconfirmed.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            await Assert.That(unconfirmed.Headers.Location!.AbsolutePath.StartsWith("/ui/terrascale/step-up/", StringComparison.Ordinal)).IsTrue();
            await Assert.That(await scenario.PendingTransferToAsync(organization)).IsNull();
        }

        using var confirmed = await HttpJourney.PostConfirmedFormAsync(scenario.Client, path, form);
        await Assert.That(confirmed.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(confirmed.Headers.Location!.OriginalString.StartsWith("/organization?tab=danger", StringComparison.Ordinal)).IsTrue();
        await Assert.That(await scenario.PendingTransferToAsync(organization)).IsEqualTo("other-user");
    }

    private sealed class Scenario : IAsyncDisposable
    {
        public required DemoEnvironment Environment { get; init; }
        public required Microsoft.AspNetCore.Builder.WebApplication App { get; init; }
        public required HttpClient Client { get; init; }

        public static async Task<Scenario> StartAsync()
        {
            var environment = new DemoEnvironment();
            await environment.InitializeAsync();
            var app = await environment.CreateTestHostAsync(new DashboardE2EFixture().Root, 0, "http://127.0.0.1");
            await app.StartAsync();
            var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new() };
            var client = new HttpClient(handler) { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
            using var login = await HttpJourney.LoginAsync(client);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            return new() { Environment = environment, App = app, Client = client };
        }

        public string AddOrganization(string name, string owner, string member)
        {
            var org = new OrganizationRecord { Id = new TerraScaleIdGenerator().NewId(), Name = name, Slug = AccountService.Slug(name), OwnerUserId = owner, CreatedAt = DateTimeOffset.UtcNow };
            org.Members[owner] = new() { UserId = owner, Email = owner + "@example.com", Role = "owner", CreatedAt = org.CreatedAt };
            org.Members[member] = new() { UserId = member, Email = member + "@example.com", Role = "member", CreatedAt = org.CreatedAt };
            lock (Environment.Workspace.Gate)
            {
                Environment.Workspace.Organizations[org.Id] = org;
            }

            return org.Id;
        }

        public async Task<string?> PendingTransferToAsync(string organization)
        {
            return (await Environment.Workspace.GetOwnershipTransferAsync(
                new(UserId, "", "user@example.com", "", null, default), organization, CancellationToken.None))?.ToUserId;
        }

        public async Task<HttpResponseMessage> PostAsync(string path, Dictionary<string, string> form)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
            request.Headers.Add("Origin", Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            request.Headers.Add("Sec-Fetch-Mode", "navigate");
            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}
