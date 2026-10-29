using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerraScale.Dashboard.Features.Accounts;

namespace TerraScale.Dashboard.E2E.Platform;

[ClassDataSource<DashboardRootFixture>]
public sealed class ReferenceJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task MatchedGoPrincipalsUseNativeSessionsAndKeepWorkspaceIsolation()
    {
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
        using var personal = scenario.CreateClient();
        using var mario = scenario.CreateClient();
        using var personalLogin = await HttpJourney.LoginAsync(personal, scenario.Personas.Routes.Email, scenario.Personas.Routes.Password);
        using var marioLogin = await HttpJourney.LoginAsync(mario, scenario.Personas.Seeded.Email, scenario.Personas.Seeded.Password);
        await Assert.That(personalLogin.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(marioLogin.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var personalIdentity = await JsonAsync(personal, "/api/v1/auth/me");
        await Assert.That(personalIdentity.GetProperty("userId").GetString()).IsEqualTo(scenario.Personas.Routes.Subject);
        await Assert.That(personalIdentity.GetProperty("name").GetString()).IsEqualTo("E2E migration-reference");
        var accountId = personalIdentity.GetProperty("accountId").GetString()!;
        await Assert.That(Regex.IsMatch(accountId, "^[0-7][0-9A-HJKMNP-TV-Z]{25}$", RegexOptions.CultureInvariant)).IsTrue();
        await Assert.That(accountId).IsEqualTo(scenario.Personas.Routes.SourceAccountId)
            .Because("The public source ULID is imported with its private subject ownership index; native sessions still authorize by provider subject.");
        var personalContext = await JsonAsync(personal, "/api/v1/dashboard/context");
        await Assert.That(personalContext.GetProperty("organizations").GetArrayLength()).IsEqualTo(1);
        await Assert.That(personalContext.GetProperty("organizations")[0].GetProperty("name").GetString()).IsEqualTo("Personal");
        using var manifest = await ReferenceFixtures.ReadManifestAsync(fixture.Root);
        await MatchCapturedPersonalAsync(manifest.RootElement.GetProperty("routes"), personalContext);
        var marioIdentity = await JsonAsync(mario, "/api/v1/auth/me");
        await Assert.That(marioIdentity.GetProperty("userId").GetString()).IsEqualTo("7CVZQJY80VY06TJG7VP8TQ44DM");
        await Assert.That(marioIdentity.GetProperty("name").GetString()).IsEqualTo("Mario Kraus");
        var marioContext = await JsonAsync(mario, "/api/v1/dashboard/context");
        await Assert.That(marioContext.GetProperty("selection").GetProperty("organizationId").GetString()).IsEqualTo("7N1PZ3BD9WCGBVV4V50V508NRX");
        await Assert.That(marioContext.GetProperty("selection").GetProperty("projectId").GetString()).IsEqualTo("1X1TETYPPH2APBVN5WGY9D3S3G");
        await Assert.That(marioContext.GetProperty("organizations").GetArrayLength()).IsEqualTo(2);
        await MatchCapturedPersonalAsync(manifest.RootElement.GetProperty("seeded"), marioContext);
        foreach (var client in new[] { personal, mario })
        {
            using var tokens = await client.GetAsync("/profile?tab=access-tokens");
            await Assert.That(tokens.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var tokenHtml = await tokens.Content.ReadAsStringAsync();
            await Assert.That(tokenHtml).Contains("No access tokens");
            await Assert.That(tokenHtml.Contains("TUnit reference manifest", StringComparison.Ordinal)).IsFalse()
                .Because("The manifest-only native PAT was issued after screenshots and revoked; it never belongs in the comparison fixtures.");
        }
        await MatchCapturedMembersAsync(personal, scenario.Personas.Routes, manifest.RootElement.GetProperty("routes"));
        await MatchCapturedMembersAsync(mario, scenario.Personas.Seeded, manifest.RootElement.GetProperty("seeded"));
        await MatchCapturedInvitationsAsync(personal, scenario.Personas.Routes, manifest.RootElement.GetProperty("routes"));
        await MatchCapturedInvitationsAsync(mario, scenario.Personas.Seeded, manifest.RootElement.GetProperty("seeded"));
        var databases = await ManagementListJourney.ReadListAllAsync(mario, "/api/v1/dashboard/databases", limit: 1);
        await Assert.That(databases.GetArrayLength()).IsEqualTo(2);
        var primary = databases.EnumerateArray().Single(database => database.GetProperty("id").GetString() == "0Z793APJ2ECFW2F7P6J9F2RKVY");
        await Assert.That(primary.GetProperty("name").GetString()).IsEqualTo("core-primary");
        var rows = (await JsonAsync(mario, "/api/v1/dashboard/data/documents?databaseId=0Z793APJ2ECFW2F7P6J9F2RKVY&collection=orders")).GetProperty("documents");
        await Assert.That(rows.GetArrayLength()).IsEqualTo(3);
        await Assert.That(rows[1].GetProperty("id").GetString()).IsEqualTo("01K78000000000000000001002");
        await Assert.That(rows[1].GetProperty("value").GetProperty("total").GetRawText()).IsEqualTo("980.0");
        using var foreignRows = await personal.GetAsync("/api/v1/dashboard/data/documents?databaseId=0Z793APJ2ECFW2F7P6J9F2RKVY&collection=orders");
        await Assert.That(foreignRows.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var deniedAdmin = await mario.GetAsync("/admin");
        await Assert.That(deniedAdmin.StatusCode).IsEqualTo(HttpStatusCode.Forbidden)
            .Because("Mario's old Go local admin flag is not an authoritative platform-admin claim.");
        using var overview = await mario.GetAsync("/?range=24h");
        await Assert.That(overview.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var html = await overview.Content.ReadAsStringAsync();
        await Assert.That(html).Contains("Sample organization activity for this demo; not measured provider traffic.");
        await Assert.That(html).Contains("<strong title=\"20,698\">20.7k</strong>");
    }

    [Test]
    public async Task SourceInvoiceHistoryUsesTheOfficialStripeProtocolAndNativePagination()
    {
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        using var login = await HttpJourney.LoginAsync(client, scenario.Personas.Seeded.Email, scenario.Personas.Seeded.Password);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var billing = await client.GetAsync("/organization?tab=billing");
        await Assert.That(billing.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await billing.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("14 recorded invoices");
        await Assert.That(WebUtility.HtmlDecode(body)).Contains("Visa •••• 4242, expires 08/28");
        await Assert.That(body).Contains("$20.00 / monthly");
        await Assert.That(body).Contains(">Choose plan</button>");
        await Assert.That(body).Contains("in_01JCA3RH5N7Q9T2W4Y6A8C2EG");
        await Assert.That(body).Contains("$41.40");
        await Assert.That(body.Contains("Payment history could not be loaded.", StringComparison.Ordinal)).IsFalse();
        using var secondPage = await client.GetAsync("/organization?tab=billing&page=2");
        await Assert.That(secondPage.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await secondPage.Content.ReadAsStringAsync()).Contains("in_01JC202508HIST12Q7T9V2X5");
        var secret = await HttpJourney.CreatePatAsync(client, "Go source invoice history", scenario.Personas.Seeded.Password);
        var billingResult = await HttpJourney.McpAsync(client, secret, "tools/call", new { name = "get_billing", arguments = new { organizationId = ReferenceFixtures.LabsId } });
        var card = billingResult.GetProperty("result").GetProperty("structuredContent").GetProperty("paymentMethod");
        await Assert.That(card.GetProperty("brand").GetString()).IsEqualTo("visa");
        await Assert.That(card.GetProperty("lastFour").GetString()).IsEqualTo("4242");
        await Assert.That(card.GetProperty("expiry").GetString()).IsEqualTo("08/28");
        var billingSummary = billingResult.GetProperty("result").GetProperty("structuredContent");
        await Assert.That(billingSummary.GetProperty("interval").GetString()).IsEqualTo("monthly");
        await Assert.That(billingSummary.GetProperty("hasSubscription").GetBoolean()).IsFalse();
        var planResult = await HttpJourney.McpAsync(client, secret, "tools/call", new { name = "list_plans", arguments = new { organizationId = ReferenceFixtures.LabsId } });
        var plans = planResult.GetProperty("result").GetProperty("structuredContent");
        await Assert.That(plans.GetProperty("currentPlanKey").GetString()).IsEqualTo("pro");
        await Assert.That(plans.GetProperty("currentInterval").GetString()).IsEqualTo("monthly");
        await Assert.That(plans.GetProperty("plans").GetArrayLength()).IsEqualTo(3);
        var pro = plans.GetProperty("plans").EnumerateArray().Single(plan => plan.GetProperty("key").GetString() == "pro");
        await Assert.That(pro.GetProperty("priceCents").GetInt64()).IsEqualTo(2000);
        await Assert.That(pro.GetProperty("annualPriceCents").GetInt64()).IsEqualTo(20000);
        await Assert.That(pro.GetProperty("price").GetString()).IsEqualTo("$20.00 / month");
        await Assert.That(pro.GetProperty("billingIntervals").EnumerateArray().Select(interval => interval.GetString()!).ToArray()).IsEquivalentTo(new[] { "month", "year" });
        var result = await HttpJourney.McpAsync(client, secret, "tools/call", new { name = "list_invoices", arguments = new { page = 2 } });
        var listing = result.GetProperty("result").GetProperty("structuredContent");
        await Assert.That(listing.GetProperty("total").GetInt32()).IsEqualTo(14);
        await Assert.That(listing.GetProperty("pageCount").GetInt32()).IsEqualTo(2);
        await Assert.That(listing.GetProperty("invoices").GetArrayLength()).IsEqualTo(4);
        var filtered = await HttpJourney.McpAsync(client, secret, "tools/call", new { name = "list_invoices", arguments = new { status = "void" } });
        var voidInvoices = filtered.GetProperty("result").GetProperty("structuredContent").GetProperty("invoices");
        await Assert.That(voidInvoices.GetArrayLength()).IsEqualTo(1);
        await Assert.That(voidInvoices[0].GetProperty("id").GetString()).IsEqualTo("in_01JC202601HIST07Q7T9V2X5");
        var choosePlanButton = System.Text.RegularExpressions.Regex.Match(body,
            "<button(?<attributes>[^>]*commandfor=\"change-billing-plan\"[^>]*)>Choose plan</button>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        await Assert.That(choosePlanButton.Success).IsTrue();
        await Assert.That(body.Contains(">Current plan</button>", StringComparison.Ordinal)).IsFalse();
        await Assert.That(choosePlanButton.Groups["attributes"].Value).Contains("ts-btn-primary");
        await Assert.That(choosePlanButton.Groups["attributes"].Value.Contains("disabled", StringComparison.Ordinal)).IsFalse();
        await Assert.That(body).Contains("name=\"plan\" value=\"pro\"");
        await Assert.That(body).Contains(">Review change</button>");
        var csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=billing");
        using var checkout = await HttpJourney.PostFormAsync(client, "/billing/checkout", new()
        { ["_csrf"] = csrf, ["plan"] = "pro", ["interval"] = "monthly", ["return"] = "/organization?tab=billing" });
        await Assert.That(checkout.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(checkout.Headers.Location?.AbsolutePath).IsEqualTo("/__test/checkout");
    }

    private static async Task<JsonElement> JsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task MatchCapturedInvitationsAsync(HttpClient client, ReferencePrincipal principal, JsonElement persona)
    {
        var secret = await HttpJourney.CreatePatAsync(client, "Captured invitations E2E", principal.Password);
        foreach (var entry in persona.GetProperty("invitations").EnumerateArray())
        {
            var response = await HttpJourney.McpAsync(client, secret, "tools/call", new
            {
                name = "list_invitations",
                arguments = new { organizationId = entry.GetProperty("organizationId").GetString() }
            });
            var result = response.GetProperty("result");
            await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse();
            var actual = result.GetProperty("structuredContent").GetProperty("invitations").EnumerateArray().ToArray();
            var captured = entry.GetProperty("result").GetProperty("invitations");
            var expectedRows = captured.ValueKind == JsonValueKind.Null ? [] : captured.EnumerateArray().ToArray();
            await Assert.That(actual.Length).IsEqualTo(expectedRows.Length);
            foreach (var expected in expectedRows)
            {
                var invitation = actual.Single(row => row.GetProperty("id").GetString() == expected.GetProperty("id").GetString());
                foreach (var field in new[] { "email", "role", "status", "expiresAt" })
                    await Assert.That(invitation.GetProperty(field).GetString()).IsEqualTo(expected.GetProperty(field).GetString());
                await Assert.That(invitation.GetProperty("open").GetBoolean()).IsEqualTo(expected.GetProperty("open").GetBoolean());
                foreach (var field in new[] { "token", "tokenHash", "respondedByUserId", "createdAt" })
                    await Assert.That(invitation.TryGetProperty(field, out _)).IsFalse()
                        .Because("Read-only captured metadata never supplies an invented timestamp or invitation credential.");
                var unavailable = await HttpJourney.McpAsync(client, secret, "tools/call", new
                {
                    name = "get_invitation",
                    arguments = new { token = expected.GetProperty("id").GetString() }
                });
                await Assert.That(unavailable.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue()
                    .Because("A source inventory ID does not become a usable invitation grant in the test application.");
            }
        }
    }

    private static async Task MatchCapturedPersonalAsync(JsonElement persona, JsonElement nativeContext)
    {
        var captured = persona.GetProperty("organizations").EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "personal");
        var actual = nativeContext.GetProperty("organizations").EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "personal");
        await Assert.That(actual.GetProperty("id").GetString()).IsEqualTo(captured.GetProperty("id").GetString());
        await Assert.That(actual.GetProperty("createdAt").GetDateTimeOffset()).IsEqualTo(captured.GetProperty("createdAt").GetDateTimeOffset());
        await Assert.That(actual.GetProperty("slug").GetString()).IsEqualTo(AccountService.Slug(captured.GetProperty("name").GetString()!));
        var project = captured.GetProperty("projects")[0];
        var actualProject = actual.GetProperty("projects").GetProperty(project.GetProperty("id").GetString()!);
        await Assert.That(actualProject.GetProperty("name").GetString()).IsEqualTo(project.GetProperty("name").GetString());
        await Assert.That(actualProject.GetProperty("region").GetString()).IsEqualTo(project.GetProperty("region").GetString());
        await Assert.That(actualProject.GetProperty("backupRegion").GetString()).IsEqualTo(project.GetProperty("backupRegion").GetString());
        await Assert.That(actualProject.GetProperty("isDefault").GetBoolean()).IsEqualTo(project.GetProperty("isDefault").GetBoolean());
    }

    private static async Task MatchCapturedMembersAsync(HttpClient client, ReferencePrincipal principal, JsonElement persona)
    {
        var secret = await HttpJourney.CreatePatAsync(client, "Captured memberships E2E", principal.Password);
        foreach (var organization in persona.GetProperty("members").EnumerateArray())
        {
            var response = await HttpJourney.McpAsync(client, secret, "tools/call", new
            {
                name = "list_members",
                arguments = new { organizationId = organization.GetProperty("organizationId").GetString() }
            });
            var result = response.GetProperty("result");
            await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse();
            var actual = result.GetProperty("structuredContent");
            var captured = organization.GetProperty("result");
            await Assert.That(actual.GetProperty("members").GetArrayLength()).IsEqualTo(captured.GetProperty("members").GetArrayLength());
            foreach (var expected in captured.GetProperty("members").EnumerateArray())
            {
                var row = actual.GetProperty("members").EnumerateArray().Single(item => item.GetProperty("id").GetString() == expected.GetProperty("id").GetString());
                foreach (var field in new[] { "email", "role" })
                    await Assert.That(row.GetProperty(field).GetString()).IsEqualTo(expected.GetProperty(field).GetString());
                await Assert.That(row.GetProperty("name").GetString()).IsEqualTo(
                    expected.GetProperty("id").GetString() == principal.Subject ? principal.Name : expected.GetProperty("email").GetString());
                // The captured Personal member has only a date; ts-auth's timestamp cannot be invented from it.
                await Assert.That(row.GetProperty("joinedAt").GetString()).IsEqualTo(
                    organization.GetProperty("organizationId").GetString() == ReferenceFixtures.LabsId ? expected.GetProperty("joinedAt").GetString() : "");
                foreach (var field in new[] { "isOwner", "isSelf", "canEdit" })
                    await Assert.That(row.GetProperty(field).GetBoolean()).IsEqualTo(expected.GetProperty(field).GetBoolean());
            }
            foreach (var role in new[] { "owner", "member", "viewer" })
                await Assert.That(actual.GetProperty("roleCounts").GetProperty(role).GetInt32()).IsEqualTo(captured.GetProperty("roleCounts").GetProperty(role).GetInt32());
        }
        var native = await JsonAsync(client, "/api/v1/dashboard/context");
        var personal = native.GetProperty("organizations").EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "personal");
        var own = personal.GetProperty("members").GetProperty(principal.Subject);
        await Assert.That(own.GetProperty("joinedOn").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(!own.TryGetProperty("createdAt", out var timestamp) || timestamp.ValueKind == JsonValueKind.Null).IsTrue()
            .Because("Canonical member metadata cannot invent a ts-auth timestamp from the source's date-only Personal record.");
    }
}
