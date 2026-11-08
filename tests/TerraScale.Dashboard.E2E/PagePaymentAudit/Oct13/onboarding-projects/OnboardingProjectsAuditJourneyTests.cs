using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerraScale.Dashboard.E2E.Features.Databases;
using Microsoft.AspNetCore.WebUtilities;

namespace TerraScale.Dashboard.E2E.PagePaymentAudit.Oct13.OnboardingProjects
{
    [ClassDataSource<DashboardE2EFixture>]
    public sealed class OnboardingProjectsAuditJourneyTests(DashboardE2EFixture fixture)
    {
        [Test]
        public async Task ProducerAuthenticatedSetupResumesAfterLoginAndValidationFailure()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var first = scenario.CreateClient();
            using var resumed = scenario.CreateClient(replica: true);
            using var login = await HttpJourney.LoginAsync(first, returnTo: "/onboarding");
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await SaveStepAsync(first, new() { ["step"] = "account-information", ["displayName"] = "Audit account" });
            using var relogin = await HttpJourney.LoginAsync(resumed, returnTo: "/onboarding");
            await Assert.That(relogin.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(await resumed.GetStringAsync("/onboarding?payment=canceled")).Contains("Payment setup canceled");
            await SaveStepAsync(resumed, new() { ["step"] = "payment-method", ["plan"] = "free" });
            using var invalid = await PostStepAsync(first, new() { ["step"] = "project", ["name"] = "Audit project", ["region"] = "ZZZ" });
            await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            var draft = await invalid.Content.ReadAsStringAsync();
            await Assert.That(draft).Contains("Choose a supported project region.");
            await Assert.That(draft).Contains("value=\"Audit project\"");
            await Assert.That(draft).Contains("value=\"ZZZ\" selected");
            await SaveStepAsync(resumed, new() { ["step"] = "project", ["name"] = "Audit project", ["region"] = "GRU" });
            var done = await JsonAsync(first, "/api/v1/auth/me");
            await Assert.That(done.GetProperty("onboardingCompleted").GetBoolean()).IsTrue();
            await SaveStepAsync(first, new() { ["step"] = "project", ["name"] = "Repeated submission", ["region"] = "SJC" });
            var context = await JsonAsync(first, "/api/v1/dashboard/context");
            var projectId = done.GetProperty("projectId").GetString()!;
            var project = context.GetProperty("organizations").EnumerateArray().SelectMany(org => org.GetProperty("projects").EnumerateObject()).Single(item => item.Name == projectId).Value;
            await Assert.That(project.GetProperty("name").GetString()).IsEqualTo("Audit project");
            await Assert.That(project.GetProperty("region").GetString()).IsEqualTo("GRU");
        }

        [Test]
        public async Task RepeatedScopedSelectionRejectsForeignIdsAndExternalReturns()
        {
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var owner = scenario.CreateClient();
            using var outsider = scenario.CreateClient();
            using var producer = scenario.CreateProviderClient();
            await DatabaseJourney.NativeWorkspaceAsync(owner);
            await DatabaseJourney.NativeWorkspaceAsync(outsider);
            var before = await JsonAsync(owner, "/api/v1/dashboard/context");
            var projectId = before.GetProperty("selection").GetProperty("projectId").GetString()!;
            var foreignId = (await JsonAsync(outsider, "/api/v1/dashboard/context")).GetProperty("selection").GetProperty("projectId").GetString()!;
            var csrf = await HttpJourney.CsrfAsync(owner, "/organization?tab=projects");
            using (var denied = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/switch-project", new()
            { ["_csrf"] = csrf, ["projectId"] = foreignId, ["returnUrl"] = "/databases" }))
            {
                await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }

            foreach (var returnUrl in new[] { "https://outside.invalid/path", "//outside.invalid/path", "/organization?tab=projects" })
            {
                using var response = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/switch-project", new()
                { ["_csrf"] = csrf, ["projectId"] = projectId, ["returnUrl"] = returnUrl });
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                var target = new Uri(owner.BaseAddress!, response.Headers.Location!);
                await Assert.That(target.AbsolutePath).IsEqualTo(returnUrl.StartsWith("/organization", StringComparison.Ordinal) ? "/organization" : "/");
                var query = QueryHelpers.ParseQuery(target.Query);
                await Assert.That(query["projectId"].ToString()).IsEqualTo(projectId);
                await Assert.That(query["organizationId"].ToString()).IsEqualTo(before.GetProperty("selection").GetProperty("organizationId").GetString());
                if (target.AbsolutePath == "/organization")
                {
                    await Assert.That(query["tab"].ToString()).IsEqualTo("projects");
                }
            }
            var after = await JsonAsync(owner, "/api/v1/dashboard/context");
            await Assert.That(after.GetProperty("selection").GetRawText()).IsEqualTo(before.GetProperty("selection").GetRawText());
            await ProtocolAcceptedAsync(producer);
        }

        private static async Task RegisterToProjectAsync(HttpClient client)
        {
            const string email = "onboarding-selection-audit@example.test";
            const string password = "Audit-local-password-123";
            using var verified = await HttpJourney.LoginAsync(client, email, password, "/onboarding", signUp: true);
            await Assert.That(verified.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await SaveStepAsync(client, new() { ["step"] = "account-information", ["displayName"] = "Audit setup" });
            await SaveStepAsync(client, new() { ["step"] = "payment-method", ["plan"] = "free" });
        }

        private static async Task CreateProjectAsync(HttpClient client, HttpClient producer, string name)
        {
            await AdmitSecondProjectAsync(client, producer);
            using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/projects", new()
            { ["_csrf"] = await HttpJourney.CsrfAsync(client, "/organization?tab=projects"), ["name"] = name, ["region"] = "GRU", ["regions"] = "GRU" });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        }

        // Test admission permits a second project; it does not simulate a paid checkout.
        private static async Task AdmitSecondProjectAsync(HttpClient client, HttpClient producer)
        {
            var organizationId = (await JsonAsync(client, "/api/v1/auth/me")).GetProperty("organizationId").GetString();
            using var admission = await producer.PostAsJsonAsync("/__test/control", new { command = "plan", organizationId, value = OrganizationRecord.StarterPlanId });
            await Assert.That(admission.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }

        private static async Task SwitchProjectAsync(HttpClient client, string projectId, string returnUrl)
        {
            using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/switch-project", new()
            { ["_csrf"] = await HttpJourney.CsrfAsync(client, "/organization?tab=projects"), ["projectId"] = projectId, ["returnUrl"] = returnUrl });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        }

        private static async Task SaveStepAsync(HttpClient client, Dictionary<string, string> form)
        {
            using var response = await PostStepAsync(client, form);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(response.Headers.Location?.OriginalString is "/onboarding" or "/").IsTrue();
        }

        private static async Task<HttpResponseMessage> PostStepAsync(HttpClient client, Dictionary<string, string> form)
        {
            form["_csrf"] = await HttpJourney.CsrfAsync(client, "/organization?tab=projects");
            return await HttpJourney.PostFormAsync(client, "/onboarding/step", form);
        }

        private static async Task ProtocolAcceptedAsync(HttpClient producer)
        {
            await Assert.That((await JsonAsync(producer, "/__test/observations")).GetProperty("failures").GetArrayLength()).IsEqualTo(0);
        }

        private static async Task<JsonElement> JsonAsync(HttpClient client, string path)
        {
            using var response = await client.GetAsync(path);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.Clone();
        }
    }
}
