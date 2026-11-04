using System.Net;
using System.Text.Json;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.E2E.Features.Accounts
{
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
            await Assert.That(await scenario.ExistsAsync(AccountService.AccountsTable, AccountService.AccountStorageKey(UserId))).IsTrue();
            await Assert.That(await scenario.ExistsAsync(AccountService.OrganizationsTable, personal)).IsTrue();
            await Assert.That(scenario.Environment.Workspace.Organizations.ContainsKey(personal)).IsTrue();
            using var profile = await scenario.Client.GetAsync("/profile?tab=danger");
            await Assert.That(profile.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        [Test]
        public async Task ConfirmedDeletionWithPopulatedDatabaseDelegatesToAuthAndKeepsSharedOrganizations()
        {
            await using var scenario = await Scenario.StartAsync();
            var personal = scenario.PersonalOrganizationId();
            var shared = scenario.AddOrganization("Shared company", owner: "other-user", member: UserId);
            var csrf = await HttpJourney.CsrfAsync(scenario.Client, "/databases");
            using var database = await HttpJourney.PostFormAsync(scenario.Client, "/api/v1/dashboard/databases", new()
            { ["_csrf"] = csrf, ["name"] = "account-data", ["homeRegion"] = "GRU" });
            await Assert.That(database.StatusCode).IsEqualTo(HttpStatusCode.Created);
            using var created = JsonDocument.Parse(await database.Content.ReadAsStringAsync());
            var databaseId = created.RootElement.GetProperty("id").GetString()!;
            foreach (var sql in new[] { "CREATE TABLE items (id INTEGER PRIMARY KEY, value INTEGER)", "INSERT INTO items (value) VALUES (1)" })
            {
                using var written = await HttpJourney.PostFormAsync(scenario.Client, "/api/v1/dashboard/data/sql", new()
                { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["sql"] = sql });
                await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

            using (var counted = await HttpJourney.PostFormAsync(scenario.Client, "/api/v1/dashboard/data/sql", new()
            { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["sql"] = "SELECT count(*) FROM items" }))
            using (var rows = JsonDocument.Parse(await counted.Content.ReadAsStringAsync()))
            {
                await Assert.That(rows.RootElement.GetProperty("rows")[0][0].GetInt32()).IsEqualTo(1);
            }
            // This journey verifies dashboard delegation; ts-auth/TerraBase qualify physical erasure.
            await HttpJourney.CreatePatAsync(scenario.Client);
            // The shared organization's projection must survive deletion.
            using (var page = await scenario.Client.GetAsync("/profile?tab=danger"))
            {
                var consent = await page.Content.ReadAsStringAsync();
                await Assert.That(consent).Contains("projects, databases and data are automatically erased from all replicas")
                    .And.Contains("Retained backups may remain until their retention period expires.")
                    .And.Contains("without a refund").And.Contains("Shared organizations keep their data and plans");
                await Assert.That(consent).DoesNotContain("Delete your databases first");
            }
            var personalProjection = await scenario.Environment.State.GetAsync(AccountService.OrganizationsTable, personal, default);
            await Assert.That(personalProjection!.Body.Deserialize<OrganizationRecord>()!.Projects.Values.Any(project => project.Databases.ContainsKey(databaseId))).IsTrue();
            await Assert.That(await scenario.ExistsAsync(AccountService.OrganizationsTable, shared)).IsTrue();

            using (var deleted = await scenario.DeleteAsync("  USER@example.com "))
            {
                await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                await Assert.That(deleted.Headers.Location!.OriginalString).IsEqualTo("/account-deleted");
            }
            var key = AccountService.AccountStorageKey(UserId);
            await Assert.That(AccountService.IsDeletionFence((await scenario.Environment.State.GetAsync(AccountService.AccountsTable, key, default))!)).IsTrue();
            await Assert.That(AccountService.IsDeletionFence((await scenario.Environment.State.GetAsync(AccountService.IdentitiesTable, key, default))!)).IsTrue();
            await Assert.That(await scenario.ExistsAsync(AccessTokens.Table, key)).IsFalse();
            await Assert.That(AccountService.IsDeletionFence((await scenario.Environment.State.GetAsync(AccountService.OrganizationsTable, personal, default))!)).IsTrue();
            await Assert.That(await scenario.ExistsAsync(AccountService.OrganizationsTable, shared)).IsTrue().Because("shared organizations keep their data");
            await Assert.That(scenario.Environment.Workspace.Organizations.ContainsKey(personal)).IsFalse();
            await Assert.That(scenario.Environment.Workspace.Organizations[shared].Members.ContainsKey(UserId)).IsFalse();
            using var session = await scenario.Client.GetAsync("/__test/session");
            await Assert.That(session.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            using var completion = await scenario.Client.GetAsync("/account-deleted");
            await Assert.That(completion.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await completion.Content.ReadAsStringAsync()).Contains("Account deleted").And.Contains("Return to TerraScale");
        }

        // The reconciler cleans the operation once ts-auth proves the deletion; the deleting browser must still see completion.
        [Test]
        public async Task CompletedDeletionStatusIsVisibleToItsBrowserOnly()
        {
            await using var scenario = await Scenario.StartAsync();
            scenario.Environment.Workspace.HoldDeletions = true;
            using (var pending = await scenario.DeleteAsync("user@example.com"))
            {
                await Assert.That(pending.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                await Assert.That(new Uri(new Uri("http://dashboard"), pending.Headers.Location!).AbsolutePath).IsEqualTo("/account-deletion");
            }

            using (var status = await scenario.Client.GetAsync("/account-deletion"))
            {
                await Assert.That(status.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
            }

            using (var cleaned = await scenario.Client.PostAsync("/__test/deletions/complete/" + UserId, null))
            {
                await Assert.That(cleaned.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            }
            using (var other = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false })
            { BaseAddress = scenario.Client.BaseAddress })
            {
                var cookie = scenario.Cookie("ts-dashboard-deletion");
                var guessed = cookie[..^1] + (cookie[^1] == '0' ? '1' : '0');
                using var request = new HttpRequestMessage(HttpMethod.Get, "/account-deletion");
                request.Headers.Add("Cookie", "ts-dashboard-deletion=" + guessed);
                using var stranger = await other.SendAsync(request);
                await Assert.That(stranger.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            }

            using var completed = await scenario.Client.GetAsync("/account-deletion");
            await Assert.That(completed.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(completed.Headers.Location!.OriginalString).IsEqualTo("/account-deleted");
        }

        [Test]
        public async Task PaidSoleOrganizationEndsAfterAuthDeletesIt()
        {
            var (scenario, userId, personal, binding) = await PaidAsync("confirmed");
            await using var _ = scenario;
            await Assert.That(scenario.Environment.BillingPlans.SubscriptionStatus(binding.SubscriptionId)).IsEqualTo("canceled");
            await Assert.That(scenario.Environment.BillingPlans.Mutations(binding.CustomerId).Count(path => path == "DELETE /v1/subscriptions/" + binding.SubscriptionId)).IsEqualTo(1);
            // The binding stays until DeletionReconciler fences it from the same auth proof.
            await Assert.That(StripeBilling.ReadBinding((await scenario.Environment.State.GetAsync(StripeBilling.Table, personal, default))!, personal).Status).IsEqualTo("canceled");
            await Assert.That(AccountService.IsDeletionFence((await scenario.Environment.State.GetAsync(AccountService.AccountsTable, AccountService.AccountStorageKey(userId), default))!)).IsTrue();
            await Assert.That(scenario.Environment.Workspace.Organizations.ContainsKey(personal)).IsFalse();
        }

        // Billing never runs before ts-auth admits the deletion, so a Stripe outage cannot fail the request.
        private static async Task<(Scenario Scenario, string UserId, string Personal, BillingBinding Binding)> PaidAsync(string stripeScenario)
        {
            var scenario = await Scenario.StartAsync(signIn: false);
            var suffix = Guid.NewGuid().ToString()[..8];
            var email = "paid-delete-" + suffix + "@example.test";
            await Billing.BillingPlanJourneyTests.SignUpPaidAsync(scenario.Client, email, "E2E-delete-" + suffix, suffix);
            var userId = scenario.Environment.Workspace.Organizations.Values.Single(org => org.Members.Values.Any(member => member.Email == email)).OwnerUserId;
            var personal = scenario.PersonalOrganizationId(userId);
            // The reported case: the owner scheduled the switch to Free, which keeps the subscription until renewal.
            var csrf = await HttpJourney.CsrfAsync(scenario.Client, "/organization?tab=billing");
            using (var free = await HttpJourney.PostFormAsync(scenario.Client, "/billing/change", new() { ["_csrf"] = csrf, ["plan"] = "free" }))
            {
                await Assert.That(free.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }

            var binding = StripeBilling.ReadBinding((await scenario.Environment.State.GetAsync(StripeBilling.Table, personal, default))!, personal);
            await Assert.That(scenario.Environment.BillingPlans.SubscriptionStatus(binding.SubscriptionId)).IsEqualTo("active");
            scenario.Environment.BillingPlans.SetScenario(binding.CustomerId, stripeScenario);
            // Without a hosted step-up proof the request only starts confirmation: nothing is deleted or billed.
            using (var unconfirmed = await HttpJourney.PostFormAsync(scenario.Client, "/api/v1/dashboard/profile/delete",
                new() { ["_csrf"] = await HttpJourney.CsrfAsync(scenario.Client, "/profile?tab=danger"), ["email"] = email }))
            {
                await Assert.That(unconfirmed.StatusCode).IsNotEqualTo(HttpStatusCode.SeeOther);
            }

            await Assert.That(await scenario.ExistsAsync(AccountService.AccountsTable, AccountService.AccountStorageKey(userId))).IsTrue();
            await Assert.That(scenario.Environment.BillingPlans.SubscriptionStatus(binding.SubscriptionId)).IsEqualTo("active");
            using (var deleted = await scenario.DeleteAsync(email))
            {
                await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                await Assert.That(deleted.Headers.Location!.OriginalString).IsEqualTo("/account-deleted");
            }
            return (scenario, userId, personal, binding);
        }

        private sealed class Scenario : IAsyncDisposable
        {
            public required DemoEnvironment Environment { get; init; }
            public required Microsoft.AspNetCore.Builder.WebApplication App { get; init; }
            public required HttpClient Client { get; init; }
            public required HttpMessageHandler Handler { get; init; }

            public static async Task<Scenario> StartAsync(bool signIn = true)
            {
                var environment = new DemoEnvironment();
                await environment.InitializeAsync();
                var app = await environment.CreateTestHostAsync(new DashboardE2EFixture().Root, 0, "http://127.0.0.1");
                await app.StartAsync();
                var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new() };
                var client = new HttpClient(handler)
                { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
                if (signIn)
                {
                    using var login = await HttpJourney.LoginAsync(client);
                    await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                }
                return new() { Environment = environment, App = app, Client = client, Handler = handler };
            }

            public string PersonalOrganizationId(string userId = UserId)
            {
                lock (Environment.Workspace.Gate)
                {
                    return Environment.Workspace.Organizations.Values.Single(org => org.Members.ContainsKey(userId)).Id;
                }
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

            public async Task<HttpResponseMessage> DeleteAsync(string email)
            {
                var csrf = await HttpJourney.CsrfAsync(Client, "/profile?tab=danger");
                return await HttpJourney.PostConfirmedFormAsync(Client, "/api/v1/dashboard/profile/delete", new() { ["_csrf"] = csrf, ["email"] = email });
            }

            public string Cookie(string name)
            {
                return ((HttpClientHandler)Handler).CookieContainer.GetCookies(Client.BaseAddress!)[name]!.Value;
            }

            public async Task<bool> ExistsAsync(string table, string id)
            {
                return await Environment.State.GetAsync(table, id, default) is not null;
            }

            public async ValueTask DisposeAsync()
            {
                Client.Dispose();
                await App.StopAsync();
                await App.DisposeAsync();
            }
        }
    }
}
