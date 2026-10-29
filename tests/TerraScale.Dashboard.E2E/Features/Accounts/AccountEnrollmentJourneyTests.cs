using System.Net;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Common.Accounts;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E.Features.Accounts
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class AccountEnrollmentJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        [Arguments("sessions")]
        [Arguments("auth_credentials")]
        [Arguments("session_indexes")]
        [Arguments(AccessTokens.IndexCollection)]
        [Arguments(AccessTokens.Collection)]
        [Arguments(AccountService.OrganizationsCollection)]
        public async Task SubstitutedReferencesCannotEraseAnotherSubjectOrAnOrganization(string collection)
        {
            await using var scenario = await AccountEnrollmentScenario.StartAsync(fixture.Root);
            using var other = await scenario.SignInAsync(scenario.Other);
            var secret = await HttpJourney.CreatePatAsync(other, password: AccountEnrollmentScenario.Password);
            var references = await scenario.ReferencesAsync(scenario.Other);
            var organization = await scenario.OrganizationAsync(scenario.Other);
            using var control = scenario.Client();
            using (var substitute = await control.PostAsync("/__test/enrollment/substitute?collection=" + collection, null))
            {
                await Assert.That(substitute.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            }

            using var replay = await control.PostAsync("/__test/enrollment/replay", null);
            await Assert.That((int)replay.StatusCode).IsGreaterThanOrEqualTo(500);
            foreach (var reference in references)
            {
                await Assert.That(await scenario.GetAsync(reference.Collection, reference.RecordId)).IsNotNull();
            }

            await Assert.That((await scenario.GetAsync(AccountService.OrganizationsCollection, organization.Id))!.Document.GetRawText())
                .IsEqualTo(organization.Document.GetRawText());
            using var retained = await other.GetAsync("/api/v1/auth/me");
            await Assert.That(retained.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var profile = await HttpJourney.McpAsync(other, secret, "tools/call", new { name = "get_profile", arguments = new { } });
            await Assert.That(profile.GetProperty("result").GetProperty("structuredContent").GetProperty("userId").GetString()).IsEqualTo(scenario.Other);
        }

        [Test]
        [Arguments("sessions")]
        [Arguments("session_indexes")]
        [Arguments("auth_credentials")]
        [Arguments(AccessTokens.IndexCollection)]
        [Arguments(AccessTokens.Collection)]
        public async Task AcknowledgedErasureReplaysReferencesForLateEnrollmentWrites(string collection)
        {
            await using var scenario = await AccountEnrollmentScenario.StartAsync(fixture.Root);
            using var owner = await scenario.SignInAsync(scenario.User);
            using var other = await scenario.SignInAsync(scenario.Other);
            var otherSecret = await HttpJourney.CreatePatAsync(other, password: AccountEnrollmentScenario.Password);
            var otherReferences = await scenario.ReferencesAsync(scenario.Other);
            var otherOrganization = await scenario.OrganizationAsync(scenario.Other);
            var ownerOrganization = await scenario.OrganizationAsync(scenario.User);
            using var stale = collection is "sessions" or "session_indexes" ? scenario.Client() : null;
            HttpResponseMessage? authorize = null;
            if (stale is null)
            {
                var csrf = await HttpJourney.CsrfAsync(owner, "/profile?tab=access-tokens");
                authorize = await HttpJourney.PostConfirmedFormAsync(owner, "/api/v1/dashboard/profile/access-tokens", new()
                { ["_csrf"] = csrf, ["name"] = "Paused enrollment", ["expiresInDays"] = "7" });
                await Assert.That(authorize.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            }
            scenario.Pause(collection);
            var enrollment = stale is not null
                ? HttpJourney.LoginAsync(stale, AccountEnrollmentScenario.Email(scenario.User), AccountEnrollmentScenario.Password)
                : HttpJourney.HostedSignInAsync(owner, authorize!, AccountEnrollmentScenario.Email(scenario.User), AccountEnrollmentScenario.Password);
            IReadOnlyList<AccountEnrollment.Reference> remembered = [];
            using var control = scenario.Client();
            try
            {
                await scenario.Paused.Task.WaitAsync(TimeSpan.FromSeconds(15));
                remembered = await scenario.ReferencesAsync(scenario.User);
                await Assert.That(remembered.Any(reference => reference.Collection == collection && reference.RecordId == scenario.PausedId)).IsTrue();
                // The journal contains exactly opaque ownership/reference IDs.
                foreach (var document in await scenario.ReferenceDocumentsAsync())
                {
                    await Assert.That(document.Document.EnumerateObject().Select(property => property.Name).Order().ToArray())
                        .IsEquivalentTo(new[] { "Collection", "RecordId", "UserId" });
                }

                using var ack = await control.PostAsync("/__test/enrollment/ack", null);
                await Assert.That(ack.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
                await Assert.That(await scenario.GetAsync(collection, scenario.PausedId)).IsNull();
                await Assert.That(await scenario.ReferencesAsync(scenario.User)).IsEquivalentTo(remembered);
            }
            finally { scenario.Resume.TrySetResult(); }
            using var denied = await enrollment;
            await Assert.That((int)denied.StatusCode).IsGreaterThanOrEqualTo(400);
            // A stale write really landed after the ACK. A fence check alone did
            // not prevent it, and an erased mutable index cannot discover it.
            var late = await scenario.GetAsync(collection, scenario.PausedId);
            await Assert.That(late).IsNotNull();
            using (var replay = await control.PostAsync("/__test/enrollment/replay", null))
            {
                await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            }

            foreach (var reference in remembered)
            {
                await Assert.That(await scenario.GetAsync(reference.Collection, reference.RecordId)).IsNull();
            }
            // References remain exact after cleanup and a further idempotent pass.
            using (var replay = await control.PostAsync("/__test/enrollment/replay", null))
            {
                await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            }

            await Assert.That(await scenario.ReferencesAsync(scenario.User)).IsEquivalentTo(remembered);
            foreach (var reference in otherReferences)
            {
                await Assert.That(await scenario.GetAsync(reference.Collection, reference.RecordId)).IsNotNull();
            }

            await Assert.That((await scenario.GetAsync(AccountService.OrganizationsCollection, otherOrganization.Id))!.Document.GetRawText())
                .IsEqualTo(otherOrganization.Document.GetRawText());
            await Assert.That((await scenario.GetAsync(AccountService.OrganizationsCollection, ownerOrganization.Id))!.Document.GetRawText())
                .IsEqualTo(ownerOrganization.Document.GetRawText());
            using var retained = await other.GetAsync("/api/v1/auth/me");
            await Assert.That(retained.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var profile = await HttpJourney.McpAsync(other, otherSecret, "tools/call", new { name = "get_profile", arguments = new { } });
            await Assert.That(profile.GetProperty("result").GetProperty("structuredContent").GetProperty("userId").GetString()).IsEqualTo(scenario.Other);
        }
    }
}
