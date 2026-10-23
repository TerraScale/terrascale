using System.Net;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.E2E.Features.Accounts;

public sealed class AccountDeletionJourneyTests
{
    private const string UserId = "test-user";

    [Test]
    public async Task WrongEmailOrSharedOwnershipRefusesAndDeletesNothing()
    {
        await using var scenario = await Scenario.StartAsync();
        var personal = scenario.PersonalOrganizationId();
        using (var wrong = await scenario.DeleteAsync("someone@example.com"))
        {
            await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            await Assert.That(await wrong.Content.ReadAsStringAsync()).Contains("Type your account email to confirm deletion.");
        }
        scenario.AddOrganization("Shared company", owner: UserId, member: "other-user");
        using (var lastOwner = await scenario.DeleteAsync("user@example.com"))
        {
            await Assert.That(lastOwner.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That(await lastOwner.Content.ReadAsStringAsync()).Contains("Transfer ownership of your shared organizations before deleting your account.");
        }
        await Assert.That(await scenario.ExistsAsync(AccountService.AccountsCollection, AccountService.AccountStorageKey(UserId))).IsTrue();
        await Assert.That(await scenario.ExistsAsync(AccountService.OrganizationsCollection, personal)).IsTrue();
        await Assert.That(scenario.Environment.Workspace.Organizations.ContainsKey(personal)).IsTrue();
        using var profile = await scenario.Client.GetAsync("/profile?tab=danger");
        await Assert.That(profile.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task ConfirmedDeletionRemovesUserSoleOrganizationsTokensAndSession()
    {
        await using var scenario = await Scenario.StartAsync();
        var personal = scenario.PersonalOrganizationId();
        var shared = scenario.AddOrganization("Shared company", owner: "other-user", member: UserId);
        await HttpJourney.CreatePatAsync(scenario.Client);
        // The first read stores the shared organization's projection.
        using (var page = await scenario.Client.GetAsync("/profile?tab=danger"))
            await Assert.That(await page.Content.ReadAsStringAsync()).Contains("Delete account");
        await Assert.That(await scenario.ExistsAsync(AccountService.OrganizationsCollection, shared)).IsTrue();

        using (var deleted = await scenario.DeleteAsync("  USER@example.com "))
        {
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(deleted.Headers.Location!.OriginalString).IsEqualTo("/login");
        }
        var key = AccountService.AccountStorageKey(UserId);
        await Assert.That(await scenario.ExistsAsync(AccountService.AccountsCollection, key)).IsFalse();
        await Assert.That(await scenario.ExistsAsync(AccountService.IdentitiesCollection, key)).IsFalse();
        await Assert.That(await scenario.ExistsAsync(AccessTokens.Collection, key)).IsFalse();
        await Assert.That(await scenario.ExistsAsync(AccountService.OrganizationsCollection, personal)).IsFalse();
        await Assert.That(await scenario.ExistsAsync(AccountService.OrganizationsCollection, shared)).IsTrue().Because("shared organizations keep their data");
        await Assert.That(scenario.Environment.Workspace.Organizations.ContainsKey(personal)).IsFalse();
        await Assert.That(scenario.Environment.Workspace.Organizations[shared].Members.ContainsKey(UserId)).IsFalse();
        using var session = await scenario.Client.GetAsync("/__test/session");
        await Assert.That(session.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
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
            var app = environment.CreateTestHost(new DashboardE2EFixture().Root, 0, "http://127.0.0.1");
            await app.StartAsync();
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new() })
            { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
            using var login = await HttpJourney.LoginAsync(client);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            return new() { Environment = environment, App = app, Client = client };
        }

        public string PersonalOrganizationId()
        {
            lock (Environment.Workspace.Gate) return Environment.Workspace.Organizations.Values.Single(org => org.Members.ContainsKey(UserId)).Id;
        }

        public string AddOrganization(string name, string owner, string member)
        {
            var org = new OrganizationRecord { Id = new TerraScaleIdGenerator().NewId(), Name = name, Slug = AccountService.Slug(name), OwnerUserId = owner, CreatedAt = DateTimeOffset.UtcNow };
            org.Members[owner] = new() { UserId = owner, Email = owner + "@example.com", Role = "owner", CreatedAt = org.CreatedAt };
            org.Members[member] = new() { UserId = member, Email = member + "@example.com", Role = "member", CreatedAt = org.CreatedAt };
            lock (Environment.Workspace.Gate) Environment.Workspace.Organizations[org.Id] = org;
            return org.Id;
        }

        public async Task<HttpResponseMessage> DeleteAsync(string email)
        {
            var csrf = await HttpJourney.CsrfAsync(Client, "/profile?tab=danger");
            return await HttpJourney.PostFormAsync(Client, "/api/v1/dashboard/profile/delete", new() { ["_csrf"] = csrf, ["email"] = email });
        }

        public async Task<bool> ExistsAsync(string collection, string id) => await Environment.State.GetAsync(collection, id, default) is not null;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}
