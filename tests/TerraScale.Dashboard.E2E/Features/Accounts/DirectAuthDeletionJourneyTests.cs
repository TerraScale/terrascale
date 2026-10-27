using System.Net;
using System.Text.Json;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.DotNet.TerraBase;

using Scenario = TerraScale.Dashboard.E2E.DirectAuthDeletionScenario;

namespace TerraScale.Dashboard.E2E.Features.Accounts;

public sealed class DirectAuthDeletionJourneyTests
{
    [Test]
    public async Task DirectDeletionWithoutDashboardOperationCleansRevokedIdentityAndRetriesLostAck()
    {
        await using var scenario = await Scenario.StartAsync();
        using var owner = await scenario.SignInAsync(scenario.User);
        using var secondSession = await scenario.SignInAsync(scenario.User);
        using var other = await scenario.SignInAsync(scenario.Other);
        var account = await scenario.AccountAsync(scenario.User);
        var shared = (await scenario.AccountAsync(scenario.Other)).PersonalOrganizationId;
        lock (scenario.Environment.Workspace.Gate)
            scenario.Environment.Workspace.Organizations[shared].Members[scenario.User] = new() { UserId = scenario.User, Email = Scenario.Email(scenario.User), Role = "member" };
        using (var sharedContext = await owner.GetAsync("/api/v1/auth/me")) sharedContext.EnsureSuccessStatusCode();
        await HttpJourney.CreatePatAsync(owner, "Direct deletion", Scenario.Password);
        var tokenSet = AccountService.Read<AccessTokens.TokenSet>((await scenario.GetAsync(AccessTokens.Collection, AccountService.AccountStorageKey(scenario.User)))!);
        var index = (await scenario.GetAsync("session_indexes", AccountService.AccountStorageKey(scenario.User)))!;
        var sessionIds = index.Document.GetProperty("Entries").EnumerateObject().Select(entry => entry.Name).ToArray();
        scenario.Auth.ChangePassword(Scenario.Email(scenario.User), "revoked-after-direct-deletion");
        using (var revoked = await owner.GetAsync("/api/v1/auth/me"))
            await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That((await scenario.State.ListAsync("account_deletions", default)).Count).IsEqualTo(0);
        scenario.Producer.Proofs.Add(new(scenario.Id(), "account", scenario.User, null));
        scenario.Producer.AckFailures = 1;
        await scenario.RunAsync();
        await Assert.That(scenario.Producer.Proofs.Count).IsEqualTo(1).Because("failed ACK retains the auth proof after local cleanup");
        foreach (var collection in new[] { AccountService.AccountsCollection, AccountService.IdentitiesCollection })
            await Assert.That(AccountService.IsDeletionFence((await scenario.GetAsync(collection, AccountService.AccountStorageKey(scenario.User)))!)).IsTrue();
        foreach (var collection in new[] { AccessTokens.Collection, "session_indexes" })
            await Assert.That(await scenario.GetAsync(collection, AccountService.AccountStorageKey(scenario.User))).IsNull();
        foreach (var id in sessionIds) await Assert.That(await scenario.GetAsync("sessions", id)).IsNull();
        foreach (var token in tokenSet.Tokens)
        {
            await Assert.That(await scenario.GetAsync("auth_credentials", token.CredentialId)).IsNull();
            await Assert.That(await scenario.GetAsync(AccessTokens.IndexCollection, token.SecretHash)).IsNull();
        }
        await Assert.That(await scenario.GetAsync(AccountService.OrganizationsCollection, account.PersonalOrganizationId)).IsNotNull();
        await Assert.That(await scenario.GetAsync(AccountService.OrganizationsCollection, shared)).IsNotNull();
        scenario.Producer.Proofs.Add(new(scenario.Id(), "organization", account.PersonalOrganizationId, account.PersonalOrganizationId));
        await scenario.RunAsync(); // A fresh consumer has no memory of the completed local cleanup.
        await Assert.That(scenario.Producer.Proofs.Count).IsEqualTo(0);
        await Assert.That(AccountService.IsDeletionFence((await scenario.GetAsync(AccountService.OrganizationsCollection, account.PersonalOrganizationId))!)).IsTrue();
        await Assert.That(await scenario.GetAsync(AccountService.OrganizationsCollection, shared)).IsNotNull();
        using var retained = await other.GetAsync("/api/v1/auth/me");
        await Assert.That(retained.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(scenario.Producer.AuthenticatedCalls).IsGreaterThan(0);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task AlreadyLoadedSessionCannotRecreateAccountOrIdentityAfterAcknowledgement(bool pauseAccount, bool productionState)
    {
        await using var scenario = await Scenario.StartAsync(productionState);
        using var owner = await scenario.SignInAsync(scenario.User);
        using var unrelated = await scenario.SignInAsync(scenario.Other);
        var key = AccountService.AccountStorageKey(scenario.User);
        foreach (var collection in new[] { AccountService.AccountsCollection, AccountService.IdentitiesCollection })
        {
            var document = (await scenario.GetAsync(collection, key))!;
            await scenario.State.DeleteAsync(collection, key, document.Version, default);
        }
        scenario.PauseCreation(pauseAccount ? AccountService.AccountsCollection : AccountService.IdentitiesCollection, key);
        var request = owner.PostAsync("/__test/deletions/ensure", null);
        try
        {
            await scenario.CreationPaused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            scenario.Auth.ChangePassword(Scenario.Email(scenario.User), "revoked-during-creation");
            scenario.Producer.Proofs.Add(new(scenario.Id(), "account", scenario.User, null));
            await scenario.RunAsync();
            await Assert.That(scenario.Producer.Acknowledgements).IsEqualTo(1);
        }
        finally { scenario.ResumeCreation.TrySetResult(); }
        using var denied = await request;
        await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(scenario.Producer.Proofs.Count).IsEqualTo(0);
        foreach (var collection in new[] { AccountService.AccountsCollection, AccountService.IdentitiesCollection })
            await Assert.That(AccountService.IsDeletionFence((await scenario.GetAsync(collection, key))!)).IsTrue();
        using var preserved = await unrelated.GetAsync("/api/v1/auth/me");
        await Assert.That(preserved.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task OrganizationProofRetainsBillingUntilStripeCancellationSucceeds()
    {
        await using var scenario = await Scenario.StartAsync();
        using var owner = scenario.Client();
        var suffix = scenario.Id().ToLowerInvariant();
        var email = "outbox-paid-" + suffix + "@example.test";
        await Billing.BillingPlanJourneyTests.SignUpPaidAsync(owner, email, Scenario.Password, suffix);
        var organizationId = scenario.Environment.Workspace.Organizations.Values.Single(org => org.Members.Values.Any(member => member.Email == email)).Id;
        var binding = StripeBilling.ReadBinding((await scenario.GetAsync(StripeBilling.Collection, organizationId))!, organizationId);
        scenario.Producer.Proofs.Add(new(scenario.Id(), "organization", organizationId, organizationId));
        scenario.Environment.BillingPlans.SetScenario(binding.CustomerId, "offline");
        await scenario.RunAsync();
        await Assert.That(scenario.Producer.Acknowledgements).IsEqualTo(0);
        await Assert.That(await scenario.GetAsync(StripeBilling.Collection, organizationId)).IsNotNull();
        await Assert.That(await scenario.GetAsync(AccountService.OrganizationsCollection, organizationId)).IsNotNull();
        scenario.Environment.BillingPlans.SetScenario(binding.CustomerId, "confirmed");
        await scenario.RunAsync();
        await Assert.That(scenario.Environment.BillingPlans.SubscriptionStatus(binding.SubscriptionId)).IsEqualTo("canceled");
        await Assert.That(AccountService.IsDeletionFence((await scenario.GetAsync(StripeBilling.Collection, organizationId))!)).IsTrue();
        await Assert.That(await scenario.GetAsync(StripeBilling.CustomersCollection, binding.CustomerId)).IsNull();
        await Assert.That(AccountService.IsDeletionFence((await scenario.GetAsync(AccountService.OrganizationsCollection, organizationId))!)).IsTrue();
        await Assert.That(scenario.Producer.Proofs.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OrganizationDeletionResolvesCompletedCheckoutWithoutPersistedSubscription(bool completeDuringExpiration)
    {
        await using var scenario = await Scenario.StartAsync();
        using var owner = scenario.Client();
        using var unrelated = await scenario.SignInAsync(scenario.Other);
        var unrelatedOrg = (await scenario.AccountAsync(scenario.Other)).PersonalOrganizationId;
        var suffix = scenario.Id().ToLowerInvariant();
        await Billing.BillingPlanJourneyTests.SignUpPaidAsync(owner, "checkout-deletion-" + suffix + "@example.test", Scenario.Password, suffix);
        var csrf = await HttpJourney.CsrfAsync(owner, "/organization?tab=billing");
        using var creation = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/organization/new-organization", new()
        { ["_csrf"] = csrf, ["step"] = "organization", ["name"] = "Deleted checkout " + suffix });
        await Assert.That(creation.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var context = JsonDocument.Parse(await owner.GetStringAsync("/api/v1/auth/me"));
        var organizationId = context.RootElement.GetProperty("organizationId").GetString()!;
        using var started = await HttpJourney.PostFormAsync(owner, "/billing/checkout", new()
        { ["_csrf"] = csrf, ["plan"] = "starter", ["interval"] = "annual", ["return"] = "/organization?tab=billing" });
        await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var binding = StripeBilling.ReadBinding((await scenario.GetAsync(StripeBilling.Collection, organizationId))!, organizationId);
        await Assert.That(binding.CheckoutId.Length).IsGreaterThan(0);
        await Assert.That(binding.SubscriptionId).IsEqualTo("");
        async Task CompleteAsync()
        {
            // Stop before the callback: Stripe creates the subscription while the dashboard still has no subscription ID.
            using var completed = await owner.GetAsync(started.Headers.Location);
            await Assert.That(completed.StatusCode).IsEqualTo(HttpStatusCode.Found);
        }
        if (completeDuringExpiration)
        {
            scenario.BeforeForwardAsync = async request =>
            {
                if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath != "/v1/checkout/sessions/" + binding.CheckoutId + "/expire") return;
                scenario.BeforeForwardAsync = null;
                await CompleteAsync();
            };
        }
        else
        {
            await CompleteAsync();
            scenario.Environment.BillingPlans.SetScenario(binding.CustomerId, "offline");
        }
        scenario.Producer.Proofs.Add(new(scenario.Id(), "organization", organizationId, organizationId));
        await scenario.RunAsync();
        await Assert.That(scenario.Producer.Acknowledgements).IsEqualTo(0);
        var pending = StripeBilling.ReadBinding((await scenario.GetAsync(StripeBilling.Collection, organizationId))!, organizationId);
        await Assert.That(pending.CheckoutId).IsEqualTo(binding.CheckoutId);
        await Assert.That(pending.SubscriptionId).IsEqualTo("");
        await Assert.That(await scenario.GetAsync(StripeBilling.CheckoutsCollection, binding.CheckoutId)).IsNotNull();
        await Assert.That(await scenario.GetAsync(StripeBilling.CustomersCollection, binding.CustomerId)).IsNotNull();
        scenario.Environment.BillingPlans.SetScenario(binding.CustomerId, "confirmed");
        await scenario.RunAsync();
        await Assert.That(scenario.Environment.BillingPlans.SubscriptionStatus("sub_" + binding.CheckoutId[3..])).IsEqualTo("canceled");
        await Assert.That(AccountService.IsDeletionFence((await scenario.GetAsync(StripeBilling.Collection, organizationId))!)).IsTrue();
        await Assert.That(AccountService.IsDeletionFence((await scenario.GetAsync(AccountService.OrganizationsCollection, organizationId))!)).IsTrue();
        await Assert.That(await scenario.GetAsync(StripeBilling.CheckoutsCollection, binding.CheckoutId)).IsNull();
        await Assert.That(await scenario.GetAsync(StripeBilling.CustomersCollection, binding.CustomerId)).IsNull();
        await Assert.That(scenario.Producer.Proofs.Count).IsEqualTo(0);
        await Assert.That(await scenario.GetAsync(AccountService.OrganizationsCollection, unrelatedOrg)).IsNotNull();
        using var retained = await unrelated.GetAsync("/api/v1/auth/me");
        await Assert.That(retained.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task InFlightCheckoutIsTerminalizedAndCannotPublishIntoDeletedBilling()
    {
        await using var scenario = await Scenario.StartAsync();
        using var owner = scenario.Client();
        var suffix = scenario.Id().ToLowerInvariant();
        await Billing.BillingPlanJourneyTests.SignUpPaidAsync(owner, "inflight-deletion-" + suffix + "@example.test", Scenario.Password, suffix);
        var csrf = await HttpJourney.CsrfAsync(owner, "/organization?tab=billing");
        using var creation = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/organization/new-organization", new()
        { ["_csrf"] = csrf, ["step"] = "organization", ["name"] = "In-flight checkout " + suffix });
        await Assert.That(creation.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var context = JsonDocument.Parse(await owner.GetStringAsync("/api/v1/auth/me"));
        var organizationId = context.RootElement.GetProperty("organizationId").GetString()!;
        scenario.PauseCheckoutPersistence(organizationId);
        var checkout = HttpJourney.PostFormAsync(owner, "/billing/checkout", new()
        { ["_csrf"] = csrf, ["plan"] = "starter", ["interval"] = "annual", ["return"] = "/organization?tab=billing" });
        try
        {
            await scenario.CreationPaused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var pending = StripeBilling.ReadBinding((await scenario.GetAsync(StripeBilling.Collection, organizationId))!, organizationId);
            await Assert.That(pending.CustomerId.Length).IsGreaterThan(0);
            await Assert.That(pending.CheckoutId).IsEqualTo("");
            scenario.Producer.Proofs.Add(new(scenario.Id(), "organization", organizationId, organizationId));
            await scenario.RunAsync();
            await Assert.That(scenario.Producer.Acknowledgements).IsEqualTo(1);
            await Assert.That(AccountService.IsDeletionFence((await scenario.GetAsync(StripeBilling.Collection, organizationId))!)).IsTrue();
            await Assert.That(await scenario.GetAsync(StripeBilling.CustomersCollection, pending.CustomerId)).IsNull();
        }
        finally { scenario.ResumeCreation.TrySetResult(); }
        using var started = await checkout;
        await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await scenario.RunAsync();
        await Assert.That(scenario.Producer.Proofs.Count).IsEqualTo(0);
        await Assert.That(AccountService.IsDeletionFence((await scenario.GetAsync(StripeBilling.Collection, organizationId))!)).IsTrue();
        await Assert.That(AccountService.IsDeletionFence((await scenario.GetAsync(AccountService.OrganizationsCollection, organizationId))!)).IsTrue();
    }

    [Test]
    public async Task CheckoutCreatedAfterAcknowledgementIsDiscoveredAndCanceledByRetainedReplay()
    {
        await using var scenario = await Scenario.StartAsync(productionState: true);
        using var owner = scenario.Client();
        using var unrelated = await scenario.SignInAsync(scenario.Other);
        var suffix = scenario.Id().ToLowerInvariant();
        await Billing.BillingPlanJourneyTests.SignUpPaidAsync(owner, "late-checkout-" + suffix + "@example.test", Scenario.Password, suffix);
        var csrf = await HttpJourney.CsrfAsync(owner, "/organization?tab=billing");
        using var creation = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/organization/new-organization", new()
        { ["_csrf"] = csrf, ["step"] = "organization", ["name"] = "Late checkout " + suffix });
        await Assert.That(creation.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var context = JsonDocument.Parse(await owner.GetStringAsync("/api/v1/auth/me"));
        var organizationId = context.RootElement.GetProperty("organizationId").GetString()!;
        string? lateCheckoutId = null;
        scenario.BeforeForwardAsync = async request =>
        {
            if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath != "/v1/checkout/sessions") return;
            scenario.CreationPaused.TrySetResult();
            await scenario.ResumeCreation.Task;
        };
        scenario.AfterForwardAsync = async (request, response) =>
        {
            if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath != "/v1/checkout/sessions") return;
            using var checkout = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            lateCheckoutId = checkout.RootElement.GetProperty("id").GetString()!;
            using var completed = await owner.GetAsync(checkout.RootElement.GetProperty("url").GetString());
            await Assert.That(completed.StatusCode).IsEqualTo(HttpStatusCode.Found);
        };
        var start = HttpJourney.PostFormAsync(owner, "/billing/checkout", new()
        { ["_csrf"] = csrf, ["plan"] = "starter", ["interval"] = "annual", ["return"] = "/organization?tab=billing" });
        string customerId;
        try
        {
            await scenario.CreationPaused.Task.WaitAsync(TimeSpan.FromSeconds(15));
            customerId = StripeBilling.ReadBinding((await scenario.GetAsync(StripeBilling.Collection, organizationId))!, organizationId).CustomerId;
            scenario.Producer.Proofs.Add(new(scenario.Id(), "organization", organizationId, organizationId));
            await scenario.RunAsync();
            await Assert.That(scenario.Producer.Acknowledgements).IsEqualTo(1);
            await Assert.That(lateCheckoutId).IsNull();
        }
        finally { scenario.ResumeCreation.TrySetResult(); }
        using var denied = await start;
        await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(lateCheckoutId).IsNotNull();
        await Assert.That(await scenario.GetAsync(StripeBilling.CustomersCollection, customerId)).IsNull();
        await Assert.That(await scenario.GetAsync(StripeBilling.CheckoutsCollection, lateCheckoutId!)).IsNull();
        scenario.Producer.Status = HttpStatusCode.NotFound;
        await scenario.RunAsync();
        await Assert.That(scenario.Environment.BillingPlans.SubscriptionStatus("sub_" + lateCheckoutId![3..])).IsEqualTo("canceled");
        await Assert.That(scenario.Producer.Acknowledgements).IsEqualTo(1);
        await Assert.That(AccountService.IsDeletionFence((await scenario.GetAsync(StripeBilling.Collection, organizationId))!)).IsTrue();
        using var preserved = await unrelated.GetAsync("/api/v1/auth/me");
        await Assert.That(preserved.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task DashboardDeletionRecoversInterruptedCheckoutBeforeCreatingAuthIntent(bool completedCheckout, bool organizationDeletion)
    {
        await using var scenario = await Scenario.StartAsync(productionState: true);
        using var owner = scenario.Client();
        using var unrelated = await scenario.SignInAsync(scenario.Other);
        var suffix = scenario.Id().ToLowerInvariant();
        var email = "preflight-recovery-" + suffix + "@example.test";
        await Billing.BillingPlanJourneyTests.SignUpPaidAsync(owner, email, Scenario.Password, suffix);
        using var personalContext = JsonDocument.Parse(await owner.GetStringAsync("/api/v1/auth/me"));
        var personalOrganization = personalContext.RootElement.GetProperty("organizationId").GetString()!;
        var personalBinding = StripeBilling.ReadBinding((await scenario.GetAsync(StripeBilling.Collection, personalOrganization))!, personalOrganization);
        var organizationName = "Interrupted checkout " + suffix;
        var csrf = await HttpJourney.CsrfAsync(owner, "/organization?tab=billing");
        using var creation = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/organization/new-organization", new()
        { ["_csrf"] = csrf, ["step"] = "organization", ["name"] = organizationName });
        await Assert.That(creation.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var context = JsonDocument.Parse(await owner.GetStringAsync("/api/v1/auth/me"));
        var organizationId = context.RootElement.GetProperty("organizationId").GetString()!;
        string? hiddenCheckoutId = null;
        scenario.AfterForwardAsync = async (request, response) =>
        {
            if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath != "/v1/checkout/sessions") return;
            using var checkout = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            hiddenCheckoutId = checkout.RootElement.GetProperty("id").GetString()!;
            if (completedCheckout)
            {
                using var completed = await owner.GetAsync(checkout.RootElement.GetProperty("url").GetString());
                await Assert.That(completed.StatusCode).IsEqualTo(HttpStatusCode.Found);
            }
            throw new HttpRequestException("Synthetic lost Checkout creation response.");
        };
        using (var interrupted = await HttpJourney.PostFormAsync(owner, "/billing/checkout", new()
        { ["_csrf"] = csrf, ["plan"] = "starter", ["interval"] = "annual", ["return"] = "/organization?tab=billing" }))
            await Assert.That(interrupted.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        scenario.AfterForwardAsync = null;
        await Assert.That(hiddenCheckoutId).IsNotNull();
        var original = StripeBilling.ReadBinding((await scenario.GetAsync(StripeBilling.Collection, organizationId))!, organizationId);
        await Assert.That(original.CheckoutId).IsEqualTo("");
        await Assert.That(original.SubscriptionId).IsEqualTo("");
        await Assert.That(await scenario.GetAsync(StripeBilling.CheckoutsCollection, hiddenCheckoutId!)).IsNull();
        scenario.BeforeForwardAsync = request => request.Method == HttpMethod.Get && request.RequestUri is { AbsolutePath: "/v1/checkout/sessions" } uri &&
            uri.Query.Contains("customer=" + Uri.EscapeDataString(original.CustomerId), StringComparison.Ordinal)
            ? throw new HttpRequestException("Synthetic Checkout outcome outage.") : Task.CompletedTask;
        var deletionPath = organizationDeletion ? "/api/v1/dashboard/organization/deletion" : "/api/v1/dashboard/profile/delete";
        var deletionForm = new Dictionary<string, string>
        { ["_csrf"] = csrf, [organizationDeletion ? "confirmation" : "email"] = organizationDeletion ? organizationName : email };
        if (!organizationDeletion)
        {
            // Billing waits for ts-auth's organization proof, so the outage neither blocks the account nor loses the hidden Checkout.
            using (var deleted = await HttpJourney.PostFormAsync(owner, deletionPath, deletionForm))
                await Assert.That(deleted.Headers.Location!.OriginalString).IsEqualTo("/account-deleted");
            var held = StripeBilling.ReadBinding((await scenario.GetAsync(StripeBilling.Collection, organizationId))!, organizationId);
            await Assert.That(held.State).IsEqualTo("deleting");
            await Assert.That(held.RequestId).IsEqualTo(original.RequestId);
            await Assert.That(scenario.Environment.Workspace.Organizations.ContainsKey(organizationId)).IsFalse();
            var expiredLater = false;
            scenario.BeforeForwardAsync = request =>
            {
                if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/checkout/sessions/" + hiddenCheckoutId + "/expire") expiredLater = true;
                return Task.CompletedTask;
            };
            scenario.Producer.Proofs.Add(new(scenario.Id(), "organization", organizationId, organizationId));
            await scenario.RunAsync();
            if (completedCheckout)
                await Assert.That(scenario.Environment.BillingPlans.SubscriptionStatus("sub_" + hiddenCheckoutId![3..])).IsEqualTo("canceled");
            else await Assert.That(expiredLater).IsTrue();
            await Assert.That(scenario.Producer.Acknowledgements).IsEqualTo(1);
            await Assert.That(AccountService.IsDeletionFence((await scenario.GetAsync(StripeBilling.Collection, organizationId))!)).IsTrue();
            using var unaffected = await unrelated.GetAsync("/api/v1/auth/me");
            await Assert.That(unaffected.StatusCode).IsEqualTo(HttpStatusCode.OK);
            return;
        }
        using (var pending = await HttpJourney.PostFormAsync(owner, deletionPath, deletionForm))
            await Assert.That(pending.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(AccountService.Read<OrganizationRecord>((await scenario.GetAsync(AccountService.OrganizationsCollection, organizationId))!).DeletionRequested).IsTrue();
        var retained = StripeBilling.ReadBinding((await scenario.GetAsync(StripeBilling.Collection, organizationId))!, organizationId);
        await Assert.That(retained.State).IsEqualTo("deleting");
        await Assert.That(retained.RequestId).IsEqualTo(original.RequestId);
        await Assert.That(retained.CustomerId).IsEqualTo(original.CustomerId);
        await Assert.That(retained.CheckoutId).IsEqualTo("");
        await Assert.That(await scenario.GetAsync(StripeBilling.CustomersCollection, original.CustomerId)).IsNotNull();
        await Assert.That((await scenario.State.ListAsync("account_deletions", default)).Count).IsEqualTo(0);
        await Assert.That(scenario.Environment.Workspace.Organizations.ContainsKey(organizationId)).IsTrue();
        using (var stalePublication = await HttpJourney.PostFormAsync(owner, "/billing/checkout", new()
        { ["_csrf"] = csrf, ["plan"] = "starter", ["interval"] = "annual", ["return"] = "/organization?tab=billing" }))
            await Assert.That(stalePublication.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        var expired = false;
        scenario.BeforeForwardAsync = request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/checkout/sessions/" + hiddenCheckoutId + "/expire") expired = true;
            return Task.CompletedTask;
        };
        using (var deleted = await HttpJourney.PostFormAsync(owner, deletionPath, deletionForm))
        {
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(deleted.Headers.Location!.OriginalString).StartsWith("/organization?deleted=1");
        }
        if (completedCheckout)
            await Assert.That(scenario.Environment.BillingPlans.SubscriptionStatus("sub_" + hiddenCheckoutId![3..])).IsEqualTo("canceled");
        else await Assert.That(expired).IsTrue();
        await Assert.That(scenario.Environment.Workspace.Organizations.ContainsKey(organizationId)).IsFalse();
        await Assert.That(scenario.Environment.Workspace.Organizations.ContainsKey(personalOrganization)).IsTrue();
        await Assert.That(scenario.Environment.BillingPlans.SubscriptionStatus(personalBinding.SubscriptionId)).IsEqualTo("active");
        using var retainedOwner = await owner.GetAsync("/api/v1/auth/me");
        await Assert.That(retainedOwner.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(scenario.Producer.Acknowledgements).IsEqualTo(0);
        await Assert.That(scenario.Producer.Proofs.Count).IsEqualTo(0);
        using var preserved = await unrelated.GetAsync("/api/v1/auth/me");
        await Assert.That(preserved.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task PausedOrganizationHydrationCannotRecreateItsAggregateAfterAcknowledgement()
    {
        await using var scenario = await Scenario.StartAsync(productionState: true);
        using var owner = await scenario.SignInAsync(scenario.User);
        using var unrelated = await scenario.SignInAsync(scenario.Other);
        var organizationId = (await scenario.AccountAsync(scenario.User)).PersonalOrganizationId;
        var document = (await scenario.GetAsync(AccountService.OrganizationsCollection, organizationId))!;
        await scenario.State.DeleteAsync(AccountService.OrganizationsCollection, organizationId, document.Version, default);
        scenario.PauseCreation(AccountService.OrganizationsCollection, organizationId);
        var hydration = owner.PostAsync("/__test/deletions/context", null);
        try
        {
            await scenario.CreationPaused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            scenario.Producer.Proofs.Add(new(scenario.Id(), "organization", organizationId, organizationId));
            await scenario.RunAsync();
            await Assert.That(scenario.Producer.Acknowledgements).IsEqualTo(1);
        }
        finally { scenario.ResumeCreation.TrySetResult(); }
        using var denied = await hydration;
        await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(AccountService.IsDeletionFence((await scenario.GetAsync(AccountService.OrganizationsCollection, organizationId))!)).IsTrue();
        using var fresh = await owner.PostAsync("/__test/deletions/context", null);
        await Assert.That(fresh.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var preserved = await unrelated.GetAsync("/api/v1/auth/me");
        await Assert.That(preserved.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task PausedProjectMutationCannotRestoreTheDeletedProjectionAfterAcknowledgement()
    {
        await using var scenario = await Scenario.StartAsync(productionState: true);
        using var owner = await scenario.SignInAsync(scenario.User);
        var organizationId = (await scenario.AccountAsync(scenario.User)).PersonalOrganizationId;
        var original = AccountService.Read<OrganizationRecord>((await scenario.GetAsync(AccountService.OrganizationsCollection, organizationId))!);
        var projectId = original.Projects.Keys.Single();
        var attempts = 0;
        scenario.BeforeProjectMutationAsync = async org =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                scenario.CreationPaused.TrySetResult();
                await scenario.ResumeCreation.Task;
            }
            org.Projects[projectId] = new() { Id = projectId };
        };
        var mutation = owner.PostAsync("/__test/deletions/context?mutate=true", null);
        try
        {
            await scenario.CreationPaused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            scenario.Producer.Proofs.Add(new(scenario.Id(), "project", projectId, organizationId));
            await scenario.RunAsync();
            await Assert.That(scenario.Producer.Acknowledgements).IsEqualTo(1);
        }
        finally { scenario.ResumeCreation.TrySetResult(); }
        using var denied = await mutation;
        await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var current = AccountService.Read<OrganizationRecord>((await scenario.GetAsync(AccountService.OrganizationsCollection, organizationId))!);
        await Assert.That(current.Projects.ContainsKey(projectId)).IsFalse();
        await Assert.That(current.DeletedProjectIds.Contains(projectId)).IsTrue();
        using var hydrated = await owner.PostAsync("/__test/deletions/context", null);
        await Assert.That(hydrated.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await hydrated.Content.ReadAsStringAsync()).DoesNotContain(projectId);
    }

    [Test]
    public async Task ProjectProofBeforeProjectionExistsFencesAnAlreadyPausedHydration()
    {
        await using var scenario = await Scenario.StartAsync(productionState: true);
        using var owner = await scenario.SignInAsync(scenario.User);
        var organizationId = (await scenario.AccountAsync(scenario.User)).PersonalOrganizationId;
        var document = (await scenario.GetAsync(AccountService.OrganizationsCollection, organizationId))!;
        var projectId = AccountService.Read<OrganizationRecord>(document).Projects.Keys.Single();
        await scenario.State.DeleteAsync(AccountService.OrganizationsCollection, organizationId, document.Version, default);
        scenario.PauseCreation(AccountService.OrganizationsCollection, organizationId);
        var hydration = owner.PostAsync("/__test/deletions/context", null);
        try
        {
            await scenario.CreationPaused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            scenario.Producer.Proofs.Add(new(scenario.Id(), "project", projectId, organizationId));
            await scenario.RunAsync();
            await Assert.That(scenario.Producer.Acknowledgements).IsEqualTo(1);
        }
        finally { scenario.ResumeCreation.TrySetResult(); }
        using var hydrated = await hydration;
        await Assert.That(hydrated.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await hydrated.Content.ReadAsStringAsync()).DoesNotContain(projectId);
        var current = AccountService.Read<OrganizationRecord>((await scenario.GetAsync(AccountService.OrganizationsCollection, organizationId))!);
        await Assert.That(current.DeletedProjectIds.Contains(projectId)).IsTrue();
        await Assert.That(current.Projects.ContainsKey(projectId)).IsFalse();
    }

    [Test]
    public async Task ProjectProofRetriesConditionalConflictAndPreservesOtherProjectsAndOrganizations()
    {
        await using var scenario = await Scenario.StartAsync();
        using var owner = await scenario.SignInAsync(scenario.User);
        using var other = await scenario.SignInAsync(scenario.Other);
        var account = await scenario.AccountAsync(scenario.User);
        var organizationId = account.PersonalOrganizationId;
        var document = (await scenario.GetAsync(AccountService.OrganizationsCollection, organizationId))!;
        var org = AccountService.Read<OrganizationRecord>(document);
        var removed = org.Projects.Keys.Single();
        var retained = scenario.Id();
        org.Projects[retained] = new() { Id = retained };
        await scenario.State.PutAsync(AccountService.OrganizationsCollection, organizationId, JsonSerializer.SerializeToElement(org), TerraBasePrecondition.Matches(document.Version), default);
        scenario.Producer.Proofs.Add(new(scenario.Id(), "project", removed, organizationId));
        scenario.Environment.State.BeforeWriteAsync = async (collection, id) =>
        {
            if (collection != AccountService.OrganizationsCollection || id != organizationId) return;
            scenario.Environment.State.BeforeWriteAsync = null;
            var latest = (await scenario.GetAsync(collection, id))!;
            var changed = AccountService.Read<OrganizationRecord>(latest);
            changed.SpendCapCents = 123;
            await scenario.State.PutAsync(collection, id, JsonSerializer.SerializeToElement(changed), TerraBasePrecondition.Matches(latest.Version), default);
        };
        await scenario.RunAsync();
        await Assert.That(scenario.Producer.Proofs.Count).IsEqualTo(1);
        await scenario.RunAsync();
        var result = AccountService.Read<OrganizationRecord>((await scenario.GetAsync(AccountService.OrganizationsCollection, organizationId))!);
        await Assert.That(result.Projects.ContainsKey(removed)).IsFalse();
        await Assert.That(result.DeletedProjectIds.Contains(removed)).IsTrue();
        await Assert.That(result.Projects.ContainsKey(retained)).IsTrue();
        await Assert.That(result.SpendCapCents).IsEqualTo(123);
        await Assert.That(scenario.Producer.Proofs.Count).IsEqualTo(0);
        await Assert.That(await scenario.GetAsync(AccountService.OrganizationsCollection, (await scenario.AccountAsync(scenario.Other)).PersonalOrganizationId)).IsNotNull();
    }

    [Test]
    [Arguments(401)]
    [Arguments(404)]
    [Arguments(503)]
    public async Task UnavailableProducerNeverCertifiesDeletion(int status)
    {
        await using var scenario = await Scenario.StartAsync();
        using var owner = await scenario.SignInAsync(scenario.User);
        scenario.Producer.Proofs.Add(new(scenario.Id(), "account", scenario.User, null));
        scenario.Producer.Status = (HttpStatusCode)status;
        await scenario.RunAsync();
        await Assert.That(await scenario.AccountAsync(scenario.User)).IsNotNull();
        await Assert.That(scenario.Producer.Proofs.Count).IsEqualTo(1);
        await Assert.That(scenario.Producer.Acknowledgements).IsEqualTo(0);
    }

    [Test]
    public async Task MismatchedOrganizationProofIsNotAcknowledgedOrApplied()
    {
        await using var scenario = await Scenario.StartAsync();
        using var owner = await scenario.SignInAsync(scenario.User);
        using var other = await scenario.SignInAsync(scenario.Other);
        var org = (await scenario.AccountAsync(scenario.User)).PersonalOrganizationId;
        var unrelated = (await scenario.AccountAsync(scenario.Other)).PersonalOrganizationId;
        scenario.Producer.Proofs.Add(new(scenario.Id(), "organization", org, unrelated));
        await scenario.RunAsync();
        await Assert.That(await scenario.GetAsync(AccountService.OrganizationsCollection, org)).IsNotNull();
        await Assert.That(await scenario.GetAsync(AccountService.OrganizationsCollection, unrelated)).IsNotNull();
        await Assert.That(scenario.Producer.Acknowledgements).IsEqualTo(0);
    }
}
