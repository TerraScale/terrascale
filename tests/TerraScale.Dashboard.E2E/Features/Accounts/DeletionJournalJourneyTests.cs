using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Features.Billing.Internal;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.Dashboard.Features.Logs;
using TerraScale.Dashboard.Features.Notifications;
using TerraScale.Dashboard.Features.Profile;
using TerraScale.DotNet.TerraBase;

using Scenario = TerraScale.Dashboard.E2E.DirectAuthDeletionScenario;

namespace TerraScale.Dashboard.E2E.Features.Accounts
{
    public sealed class DeletionJournalJourneyTests
    {
        [Test]
        public async Task AcknowledgedAccountProofRepairsLaterRegionalValuesWhileAuthIsUnavailable()
        {
            await using var scenario = await Scenario.StartAsync(productionState: true);
            using var owner = await scenario.SignInAsync(scenario.User);
            using var unrelated = await scenario.SignInAsync(scenario.Other);
            await HttpJourney.CreatePatAsync(owner, password: Scenario.Password);
            await HttpJourney.CreatePatAsync(unrelated, password: Scenario.Password);
            var codeKey = AccountService.AccountStorageKey(scenario.Id());
            var otherCodeKey = AccountService.AccountStorageKey(scenario.Id());
            JsonElement pendingCode = default;
            foreach (var (userId, id) in new[] { (scenario.User, codeKey), (scenario.Other, otherCodeKey) })
            {
                var credential = (await AccountEnrollment.ListAsync(scenario.State, userId, default)).First(reference => reference.Collection == "auth_credentials").RecordId;
                var body = JsonSerializer.SerializeToElement(new
                {
                    ClientId = scenario.Id(),
                    RedirectUri = "https://client.example.test/callback",
                    CodeChallenge = "synthetic-challenge",
                    UserId = userId,
                    CredentialId = credential,
                    ClientName = "Synthetic client",
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                    TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
                });
                await scenario.State.PutAsync(McpOAuth.CodeCollection, id, body, TerraBasePrecondition.Absent, default);
                if (userId == scenario.User)
                {
                    pendingCode = body;
                }
            }
            var key = AccountService.AccountStorageKey(scenario.User);
            var account = (await scenario.GetAsync(AccountService.AccountsCollection, key))!;
            var identity = (await scenario.GetAsync(AccountService.IdentitiesCollection, key))!;
            var index = (await scenario.GetAsync("session_indexes", key))!;
            var sessionId = index.Document.GetProperty("Entries").EnumerateObject().First().Name;
            var session = (await scenario.GetAsync("sessions", sessionId))!;
            var proofId = scenario.Id();
            scenario.Producer.Proofs.Add(new(proofId, "account", scenario.User, null));
            await scenario.RunAsync();
            await Assert.That(scenario.Producer.Acknowledgements).IsEqualTo(1);
            var journal = (await scenario.GetAsync(DeletionCleanupJournal.Collection, proofId))!;
            await Assert.That(await scenario.GetAsync(McpOAuth.CodeCollection, codeKey)).IsNull();
            await Assert.That(await scenario.GetAsync(McpOAuth.CodeCollection, otherCodeKey)).IsNotNull();
            await scenario.State.PutAsync(McpOAuth.CodeCollection, codeKey, pendingCode, TerraBasePrecondition.Absent, default);
            foreach (var (collection, original) in new[] { (AccountService.AccountsCollection, account), (AccountService.IdentitiesCollection, identity) })
            {
                var fence = (await scenario.GetAsync(collection, key))!;
                // Inject the value a delayed regional LWW join could restore; application writers cannot replace this fence.
                await scenario.State.PutAsync(collection, key, original.Document, TerraBasePrecondition.Matches(fence.Version), default);
            }
            await scenario.State.PutAsync("sessions", sessionId, session.Document, TerraBasePrecondition.Absent, default);
            await scenario.State.PutAsync("session_indexes", key, index.Document, TerraBasePrecondition.Absent, default);
            // One malformed journal row must not starve another retained proof.
            await scenario.State.PutAsync(DeletionCleanupJournal.Collection, scenario.Id(), JsonSerializer.SerializeToElement(new { Proof = (object?)null }), TerraBasePrecondition.Absent, default);
            scenario.Producer.Status = HttpStatusCode.NotFound;
            await scenario.RunAsync();
            foreach (var collection in new[] { AccountService.AccountsCollection, AccountService.IdentitiesCollection })
            {
                await Assert.That(AccountService.IsDeletionFence((await scenario.GetAsync(collection, key))!)).IsTrue();
            }

            await Assert.That(await scenario.GetAsync("sessions", sessionId)).IsNull();
            await Assert.That(await scenario.GetAsync("session_indexes", key)).IsNull();
            await Assert.That(await scenario.GetAsync(McpOAuth.CodeCollection, codeKey)).IsNull();
            await Assert.That(await scenario.GetAsync(McpOAuth.CodeCollection, otherCodeKey)).IsNotNull();
            var retained = (await scenario.GetAsync(DeletionCleanupJournal.Collection, proofId))!;
            await Assert.That(retained.Version).IsEqualTo(journal.Version);
            await Assert.That(retained.Document.GetRawText()).IsEqualTo(journal.Document.GetRawText());
            await Assert.That(scenario.Producer.Acknowledgements).IsEqualTo(1);
            using var preserved = await unrelated.GetAsync("/api/v1/auth/me");
            await Assert.That(preserved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        [Test]
        public async Task AccountReplayErasesEncryptedOperationsAndRejectsASubstitutedPlaintextOwner()
        {
            await using var scenario = await Scenario.StartAsync(productionState: true);
            using var owner = await scenario.SignInAsync(scenario.User);
            using var other = await scenario.SignInAsync(scenario.Other);
            var operation = await scenario.StoreDeletionOperationAsync(scenario.User);
            var retained = await scenario.StoreDeletionOperationAsync(scenario.Other);
            scenario.Producer.Proofs.Add(new(scenario.Id(), "account", scenario.User, null));
            await scenario.RunAsync();
            await Assert.That(scenario.Producer.Acknowledgements).IsEqualTo(1);
            await Assert.That(await scenario.GetAsync("account_deletions", operation.Id)).IsNull();
            await Assert.That((await scenario.GetAsync("account_deletions", retained.Id))!.Document.GetRawText()).IsEqualTo(retained.Document.GetRawText());
            await scenario.State.PutAsync("account_deletions", operation.Id, operation.Document, TerraBasePrecondition.Absent, default);
            scenario.Producer.Status = HttpStatusCode.NotFound;
            await scenario.RunAsync();
            await Assert.That(await scenario.GetAsync("account_deletions", operation.Id)).IsNull();
            var substituted = JsonSerializer.SerializeToElement(new
            {
                Key = retained.Id,
                UserId = scenario.User,
                Payload = retained.Document.GetProperty("Payload")
            });
            await scenario.State.PutAsync("account_deletions", retained.Id, substituted, TerraBasePrecondition.Matches(retained.Version), default);
            var substitutedDocument = (await scenario.GetAsync("account_deletions", retained.Id))!;
            await scenario.RunAsync();
            var untouched = (await scenario.GetAsync("account_deletions", retained.Id))!;
            await Assert.That(untouched.Version).IsEqualTo(substitutedDocument.Version);
            await Assert.That(untouched.Document.GetRawText()).IsEqualTo(substitutedDocument.Document.GetRawText());
            await Assert.That(scenario.Producer.Acknowledgements).IsEqualTo(1);
            using var preserved = await other.GetAsync("/api/v1/auth/me");
            await Assert.That(preserved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        [Test]
        public async Task JournalWriteFailurePreventsCleanupAndAcknowledgement()
        {
            await using var scenario = await Scenario.StartAsync();
            using var owner = await scenario.SignInAsync(scenario.User);
            var key = AccountService.AccountStorageKey(scenario.User);
            var original = (await scenario.GetAsync(AccountService.AccountsCollection, key))!;
            scenario.Producer.Proofs.Add(new(scenario.Id(), "account", scenario.User, null));
            scenario.Environment.State.WriteFailure = (collection, _) => collection == DeletionCleanupJournal.Collection ? new IOException("Test journal outage.") : null;
            await scenario.RunAsync();
            await Assert.That(scenario.Producer.Acknowledgements).IsEqualTo(0);
            await Assert.That((await scenario.GetAsync(AccountService.AccountsCollection, key))!.Version).IsEqualTo(original.Version);
            scenario.Environment.State.WriteFailure = null;
            await scenario.RunAsync();
            await Assert.That(scenario.Producer.Acknowledgements).IsEqualTo(1);
        }

        [Test]
        public async Task OrganizationReplayCleansHistoricalMappingsLedgersAndOrphanProjectIndexes()
        {
            await using var scenario = await Scenario.StartAsync(productionState: true);
            using var owner = await scenario.SignInAsync(scenario.User);
            using var other = await scenario.SignInAsync(scenario.Other);
            var organizationId = (await scenario.AccountAsync(scenario.User)).PersonalOrganizationId;
            var otherOrganization = (await scenario.AccountAsync(scenario.Other)).PersonalOrganizationId;
            var original = (await scenario.GetAsync(AccountService.OrganizationsCollection, organizationId))!;
            var projectId = AccountService.Read<OrganizationRecord>(original).Projects.Keys.Single();
            var orphanProject = scenario.Id();
            var retainedProject = scenario.Id();
            var checkout = new CheckoutBinding("cs_historical", organizationId, "cus_historical", "historical-request", projectId, "/", "starter", "annual");
            var rows = new[]
            {
                (StripeBilling.CheckoutsCollection, checkout.CheckoutId, JsonSerializer.SerializeToElement(checkout)),
                (StripeBilling.CustomersCollection, checkout.CustomerId, JsonSerializer.SerializeToElement(new CustomerBinding(organizationId))),
                (StripeBilling.ChangesCollection, organizationId, JsonSerializer.SerializeToElement(new BillingChangeReceipt { OrganizationId = organizationId })),
                (AllowanceNotifications.Collection, organizationId, JsonSerializer.SerializeToElement(new AllowanceNotificationState(organizationId, [])))
            };
            foreach (var (collection, id, body) in rows)
            {
                await scenario.State.PutAsync(collection, id, body, TerraBasePrecondition.Absent, default);
            }

            foreach (var (collection, body) in new[]
            {
                (StripeBilling.CustomersCollection, JsonSerializer.SerializeToElement(new CustomerBinding(otherOrganization))),
                (StripeBilling.ChangesCollection, JsonSerializer.SerializeToElement(new BillingChangeReceipt { OrganizationId = otherOrganization })),
                (AllowanceNotifications.Collection, JsonSerializer.SerializeToElement(new AllowanceNotificationState(otherOrganization, [])))
            })
            {
                await scenario.State.PutAsync(collection, otherOrganization, body, TerraBasePrecondition.Absent, default);
            }

            var index = JsonSerializer.SerializeToElement(new AlertProjectIndex(new() { [orphanProject] = organizationId, [retainedProject] = otherOrganization }));
            await scenario.State.PutAsync(AlertRules.IndexCollection, "all", index, TerraBasePrecondition.Absent, default);
            var otherKey = AccountService.AccountStorageKey(scenario.Other);
            var otherDocument = (await scenario.GetAsync(AccountService.AccountsCollection, otherKey))!;
            var account = AccountService.Read<AccountRecord>(otherDocument);
            account.Notifications.Add(new() { Id = scenario.Id(), OrganizationId = organizationId, Title = "Deleted organization" });
            account.Notifications.Add(new() { Id = scenario.Id(), OrganizationId = otherOrganization, Title = "Retained organization" });
            await scenario.State.PutAsync(AccountService.AccountsCollection, otherKey, JsonSerializer.SerializeToElement(account), TerraBasePrecondition.Matches(otherDocument.Version), default);
            scenario.Producer.Proofs.Add(new(scenario.Id(), "organization", organizationId, organizationId));
            await scenario.RunAsync();
            await Assert.That(scenario.Producer.Acknowledgements).IsEqualTo(1);
            foreach (var (collection, id, _) in rows)
            {
                await Assert.That(await scenario.GetAsync(collection, id)).IsNull();
            }

            var fence = (await scenario.GetAsync(AccountService.OrganizationsCollection, organizationId))!;
            await Assert.That(AccountService.IsDeletionFence(fence)).IsTrue();
            // A later join restores historical rows after their natural-key mappings are gone.
            await scenario.State.PutAsync(AccountService.OrganizationsCollection, organizationId, original.Document, TerraBasePrecondition.Matches(fence.Version), default);
            foreach (var (collection, id, body) in rows)
            {
                await scenario.State.PutAsync(collection, id, body, TerraBasePrecondition.Absent, default);
            }

            var currentIndex = (await scenario.GetAsync(AlertRules.IndexCollection, "all"))!;
            await scenario.State.PutAsync(AlertRules.IndexCollection, "all", index, TerraBasePrecondition.Matches(currentIndex.Version), default);
            scenario.Producer.Status = HttpStatusCode.ServiceUnavailable;
            await scenario.RunAsync();
            foreach (var (collection, id, _) in rows)
            {
                await Assert.That(await scenario.GetAsync(collection, id)).IsNull();
            }

            foreach (var collection in new[] { StripeBilling.CustomersCollection, StripeBilling.ChangesCollection, AllowanceNotifications.Collection })
            {
                await Assert.That(await scenario.GetAsync(collection, otherOrganization)).IsNotNull();
            }

            await Assert.That(AccountService.IsDeletionFence((await scenario.GetAsync(AccountService.OrganizationsCollection, organizationId))!)).IsTrue();
            var remaining = AccountService.Read<AlertProjectIndex>((await scenario.GetAsync(AlertRules.IndexCollection, "all"))!).Projects;
            await Assert.That(remaining.Count).IsEqualTo(1);
            await Assert.That(remaining[retainedProject]).IsEqualTo(otherOrganization);
            var notifications = AccountService.Read<AccountRecord>((await scenario.GetAsync(AccountService.AccountsCollection, otherKey))!).Notifications;
            await Assert.That(notifications.Count).IsEqualTo(1);
            await Assert.That(notifications.Single().Title).IsEqualTo("Retained organization");
            using var preserved = await other.GetAsync("/api/v1/auth/me");
            await Assert.That(preserved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        [Test]
        public async Task RetainedOrganizationProofReleasesOnlyItsInternalBillingSingleton()
        {
            await using var scenario = await Scenario.StartAsync(productionState: true);
            using var owner = await scenario.SignInAsync(scenario.User);
            using var other = await scenario.SignInAsync(scenario.Other);
            var organizationId = (await scenario.AccountAsync(scenario.User)).PersonalOrganizationId;
            var otherOrganization = (await scenario.AccountAsync(scenario.Other)).PersonalOrganizationId;
            var owned = JsonSerializer.SerializeToElement(new InternalBillingSingleton(organizationId, scenario.Id()));
            await scenario.State.PutAsync(InternalBillingService.Collection, InternalBillingService.SingletonId, owned, TerraBasePrecondition.Absent, default);
            scenario.Producer.Proofs.Add(new(scenario.Id(), "organization", organizationId, organizationId));
            await scenario.RunAsync();
            await Assert.That(scenario.Producer.Acknowledgements).IsEqualTo(1);
            await Assert.That(await scenario.GetAsync(InternalBillingService.Collection, InternalBillingService.SingletonId)).IsNull();
            // A retained proof also releases a late regional rewrite after ACK.
            scenario.Producer.Status = HttpStatusCode.ServiceUnavailable;
            await scenario.State.PutAsync(InternalBillingService.Collection, InternalBillingService.SingletonId, owned, TerraBasePrecondition.Absent, default);
            await scenario.RunAsync();
            await Assert.That(await scenario.GetAsync(InternalBillingService.Collection, InternalBillingService.SingletonId)).IsNull();
            // The same singleton key can now be allocated to a replacement organization.
            var replacement = JsonSerializer.SerializeToElement(new InternalBillingSingleton(otherOrganization, scenario.Id()));
            await scenario.State.PutAsync(InternalBillingService.Collection, InternalBillingService.SingletonId, replacement, TerraBasePrecondition.Absent, default);
            var retained = (await scenario.GetAsync(InternalBillingService.Collection, InternalBillingService.SingletonId))!;
            await scenario.RunAsync();
            var untouched = (await scenario.GetAsync(InternalBillingService.Collection, InternalBillingService.SingletonId))!;
            await Assert.That(untouched.Version).IsEqualTo(retained.Version);
            await Assert.That(untouched.Document.GetRawText()).IsEqualTo(retained.Document.GetRawText());
            await Assert.That(scenario.Producer.Acknowledgements).IsEqualTo(1);
            using var preserved = await other.GetAsync("/api/v1/auth/me");
            await Assert.That(preserved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        [Test]
        public async Task ProjectReplayRemovesLateOwnedRecordsAndRetainsUnrelatedIndexesAndNotifications()
        {
            await using var scenario = await Scenario.StartAsync(productionState: true);
            using var owner = await scenario.SignInAsync(scenario.User);
            using var other = await scenario.SignInAsync(scenario.Other);
            var orgId = (await scenario.AccountAsync(scenario.User)).PersonalOrganizationId;
            var otherOrg = (await scenario.AccountAsync(scenario.Other)).PersonalOrganizationId;
            var original = (await scenario.GetAsync(AccountService.OrganizationsCollection, orgId))!;
            var org = AccountService.Read<OrganizationRecord>(original);
            var project = org.Projects.Keys.Single();
            var retainedProject = scenario.Id();
            var reservation = scenario.Id();
            org.InvitationReservations[reservation] = new("invited@example.test", DateTimeOffset.UtcNow) { ProjectId = project };
            await scenario.State.PutAsync(AccountService.OrganizationsCollection, orgId, JsonSerializer.SerializeToElement(org), TerraBasePrecondition.Matches(original.Version), default);
            var otherKey = AccountService.AccountStorageKey(scenario.Other);
            var otherDocument = (await scenario.GetAsync(AccountService.AccountsCollection, otherKey))!;
            var otherAccount = AccountService.Read<AccountRecord>(otherDocument);
            otherAccount.Notifications.Add(new() { Id = scenario.Id(), OrganizationId = orgId, ProjectId = project, Title = "Deleted scope" });
            otherAccount.Notifications.Add(new() { Id = scenario.Id(), OrganizationId = otherOrg, ProjectId = retainedProject, Title = "Retained scope" });
            await scenario.State.PutAsync(AccountService.AccountsCollection, otherKey, JsonSerializer.SerializeToElement(otherAccount), TerraBasePrecondition.Matches(otherDocument.Version), default);
            var queryKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(orgId + "/" + project)));
            var drain = new LogDrain(project, orgId, scenario.Id(), "https://drain.example.test", null, null, DateTimeOffset.UtcNow);
            var rules = new AlertRuleSet(project, orgId, []);
            var queries = new SavedQueryRecord(orgId, project, []);
            var indexes = new Dictionary<string, string> { [project] = orgId, [retainedProject] = otherOrg };
            foreach (var (collection, id, body) in new[]
            {
                ("dashboard_saved_queries", queryKey, JsonSerializer.SerializeToElement(queries)),
                (AlertRules.Collection, project, JsonSerializer.SerializeToElement(rules)),
                (LogDrains.Collection, project, JsonSerializer.SerializeToElement(drain)),
                (AlertRules.IndexCollection, "all", JsonSerializer.SerializeToElement(new AlertProjectIndex(indexes))),
                (LogDrains.IndexCollection, "all", JsonSerializer.SerializeToElement(new LogDrainIndex(indexes)))
            })
            {
                await scenario.State.PutAsync(collection, id, body, TerraBasePrecondition.Absent, default);
            }

            scenario.Producer.Proofs.Add(new(scenario.Id(), "project", project, orgId));
            await scenario.RunAsync();
            await Assert.That(scenario.Producer.Acknowledgements).IsEqualTo(1);
            var cleaned = AccountService.Read<OrganizationRecord>((await scenario.GetAsync(AccountService.OrganizationsCollection, orgId))!);
            await Assert.That(cleaned.InvitationReservations.ContainsKey(reservation)).IsFalse();
            await Assert.That(cleaned.DeletedProjectIds.Contains(project)).IsTrue();
            var notifications = AccountService.Read<AccountRecord>((await scenario.GetAsync(AccountService.AccountsCollection, otherKey))!).Notifications;
            await Assert.That(notifications.Count).IsEqualTo(1);
            await Assert.That(notifications.Single().Title).IsEqualTo("Retained scope");
            await scenario.State.PutAsync("dashboard_saved_queries", queryKey, JsonSerializer.SerializeToElement(queries), TerraBasePrecondition.Absent, default);
            await scenario.State.PutAsync(LogDrains.Collection, project, JsonSerializer.SerializeToElement(drain), TerraBasePrecondition.Absent, default);
            scenario.Producer.Status = HttpStatusCode.ServiceUnavailable;
            await scenario.RunAsync();
            await Assert.That(await scenario.GetAsync("dashboard_saved_queries", queryKey)).IsNull();
            await Assert.That(await scenario.GetAsync(LogDrains.Collection, project)).IsNull();
            foreach (var collection in new[] { AlertRules.IndexCollection, LogDrains.IndexCollection })
            {
                var current = AccountService.Read<AlertProjectIndex>((await scenario.GetAsync(collection, "all"))!).Projects;
                await Assert.That(current.Count).IsEqualTo(1);
                await Assert.That(current[retainedProject]).IsEqualTo(otherOrg);
            }
        }
    }
}
