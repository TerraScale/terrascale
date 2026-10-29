using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.E2E.PagePaymentAudit.Oct13.AccountsMembersSettings;

[ClassDataSource<AccountsAuditScenario>]
public sealed class AccountsMembersSettingsAuditJourneyTests(AccountsAuditScenario scenario)
{
    [Test]
    [Category("AuthorizationPolicyQuestion")]
    [Arguments("members")]
    [Arguments("invitations")]
    public async Task CandidatePolicyOrdinaryMembersCannotReadInvitationHistory(string tab)
    {
        using var client = await scenario.LoginAsync("member");
        using var response = await client.GetAsync("/organization?tab=" + tab);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        await Assert.That(html.Contains("id=\"team-invite\"", StringComparison.Ordinal)).IsFalse();
        await Assert.That(html.Contains("invite-00@accounts-audit.example.test", StringComparison.Ordinal)).IsFalse()
            .Because("Candidate policy: canonical history follows the provider's management-only invitation list. Confirm intended role policy before treating this as a defect.");
    }

    [Test]
    public async Task InviteAndMemberRoleChoicesMatchTheSupportedProviderRoles()
    {
        using var client = await scenario.LoginAsync();
        var html = await client.GetStringAsync("/organization?tab=members");
        var selectors = Regex.Matches(html, "<select[^>]+name=\"role\"[^>]*>(.*?)</select>", RegexOptions.Singleline);
        await Assert.That(selectors.Count).IsGreaterThan(0);
        foreach (Match selector in selectors)
        {
            var values = Regex.Matches(selector.Groups[1].Value, "<option value=\"([^\"]+)\"")
                .Select(item => item.Groups[1].Value).ToArray();
            await Assert.That(values.Contains("viewer", StringComparer.Ordinal)).IsTrue()
                .Because("Viewer is supported by the current ts-auth invitation and membership contracts.");
            await Assert.That(values.Contains("admin", StringComparer.Ordinal)).IsTrue()
                .Because("Admin is an available provider role, including for an existing administrator.");
        }
    }

    [Test]
    public async Task InvalidProfilePreservesDraftFieldsWithoutWritingTheAccount()
    {
        using var client = await scenario.LoginAsync();
        var before = await client.GetStringAsync("/api/v1/auth/me");
        var csrf = await HttpJourney.CsrfAsync(client, "/profile");
        var phone = new string('9', 65);
        using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/profile", new()
        { ["_csrf"] = csrf, ["name"] = "Draft display name", ["phoneNumber"] = phone });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        await Assert.That(await client.GetStringAsync("/api/v1/auth/me")).IsEqualTo(before);
        var html = await response.Content.ReadAsStringAsync();
        await Assert.That(html.Contains("Enter a valid phone number.", StringComparison.Ordinal)).IsTrue();
        await Assert.That(InputValue(html, "name")).IsEqualTo("Draft display name")
            .Because("A validation error must keep the user's unsaved name so only the invalid phone needs correcting.");
        await Assert.That(InputValue(html, "phoneNumber")).IsEqualTo(phone);
    }

    [Test]
    public async Task MemberAndInvitationPagesClampAndFiltersHaveUsefulEmptyStates()
    {
        using var client = await scenario.LoginAsync();
        var pageOne = await client.GetStringAsync("/organization?tab=members");
        var pageTwo = await client.GetStringAsync("/organization?tab=invitations&page=999999");
        await Assert.That(TableRows(pageOne, "team-members")).IsEqualTo(10);
        await Assert.That(TableRows(pageOne, "team-invitations")).IsEqualTo(10);
        await Assert.That(TableRows(pageTwo, "team-members")).IsEqualTo(3);
        await Assert.That(TableRows(pageTwo, "team-invitations")).IsEqualTo(2);
        await Assert.That(pageTwo.Contains("Showing 11–12 of 12", StringComparison.Ordinal)).IsTrue();
        var noMatch = await client.GetStringAsync("/organization?tab=members&q=not-a-real-member&role=member&page=99");
        await Assert.That(noMatch.Contains("No members match", StringComparison.Ordinal)).IsTrue();
        await Assert.That(noMatch.Contains("Clear filters", StringComparison.Ordinal)).IsTrue();
        var filtered = await client.GetStringAsync("/organization?tab=members&q=page-member&role=member&page=99");
        await Assert.That(TableRows(filtered, "team-members")).IsEqualTo(10);
        var filteredMembers = Regex.Match(filtered, "id=\"team-members\".*?<tbody>(.*?)</tbody>", RegexOptions.Singleline).Groups[1].Value;
        await Assert.That(filteredMembers.Contains(AccountsAuditScenario.OwnerId, StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task RepeatedUnsupportedRolesAndDeniedMemberActionsLeaveIdentityUnchanged()
    {
        using var owner = await scenario.LoginAsync();
        var ownerCsrf = await HttpJourney.CsrfAsync(owner, "/organization?tab=members");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var invite = await HttpJourney.PostFormAsync(owner, "/api/v1/access/invitations?tab=invitations", new()
            { ["_csrf"] = ownerCsrf, ["email"] = "never-sent@example.test", ["role"] = "invalid-role" });
            await Assert.That(invite.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            await Assert.That((await invite.Content.ReadAsStringAsync()).Contains("Choose an available role.", StringComparison.Ordinal)).IsTrue();
        }
        using var member = await scenario.LoginAsync("member");
        var before = await member.GetStringAsync("/api/v1/auth/me");
        var csrf = await HttpJourney.CsrfAsync(member, "/organization?tab=members");
        using var denied = await HttpJourney.PostFormAsync(member, "/api/v1/access/members/" + AccountsAuditScenario.AdminId + "/role?tab=members", new()
        { ["_csrf"] = csrf, ["role"] = "member" });
        await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await member.GetStringAsync("/api/v1/auth/me")).IsEqualTo(before);
        var ownerPage = await owner.GetStringAsync("/organization?tab=members");
        await Assert.That(ownerPage.Contains("never-sent@example.test", StringComparison.Ordinal)).IsFalse();
        using var admin = await scenario.LoginAsync("admin");
        using var identity = JsonDocument.Parse(await admin.GetStringAsync("/api/v1/auth/me"));
        await Assert.That(identity.RootElement.GetProperty("role").GetString()).IsEqualTo("admin");
    }

    [Test]
    public async Task RetentionSettingsDenyMembersValidateOwnersAndHideProviderDetails()
    {
        var attempts = scenario.Settings.WriteAttempts;
        using var member = await scenario.LoginAsync("member");
        var csrf = await HttpJourney.CsrfAsync(member, "/profile");
        using (var denied = await HttpJourney.PostFormAsync(member, "/api/v1/dashboard/settings/audit-retention", new()
        { ["_csrf"] = csrf, ["retentionDays"] = "30" }))
            await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using var owner = await scenario.LoginAsync();
        csrf = await HttpJourney.CsrfAsync(owner, "/profile");
        using (var invalid = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/settings/audit-retention", new()
        { ["_csrf"] = csrf, ["retentionDays"] = "91" }))
            await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(scenario.Settings.WriteAttempts).IsEqualTo(attempts);
        using var unavailable = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/settings/audit-retention", new()
        { ["_csrf"] = csrf, ["retentionDays"] = "30" });
        await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(await unavailable.Content.ReadAsStringAsync()).IsEqualTo("The provider could not complete this settings change. Try again shortly.");
        await Assert.That(scenario.Settings.WriteAttempts).IsEqualTo(attempts + 1);
    }

    [Test]
    public async Task UnavailableOrganizationProviderReturnsSafe503AndRecovers()
    {
        using var client = await scenario.LoginAsync();
        using var unavailableControl = await client.GetAsync("/__audit/access-unavailable");
        try
        {
            using var unavailable = await client.GetAsync("/organization?tab=members");
            await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That((await unavailable.Content.ReadAsStringAsync()).Contains("Synthetic private provider detail", StringComparison.Ordinal)).IsFalse();
        }
        finally { using var recoveredControl = await client.GetAsync("/__audit/access-recovered"); }
        using var recovered = await client.GetAsync("/organization?tab=members");
        await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private static string InputValue(string html, string name) => WebUtility.HtmlDecode(
        Regex.Match(html, "<input[^>]+name=\"" + name + "\"[^>]+value=\"([^\"]*)\"").Groups[1].Value);

    private static int TableRows(string html, string section)
    {
        var content = Regex.Match(html, "id=\"" + section + "\"(.*?)</section>", RegexOptions.Singleline).Groups[1].Value;
        return Regex.Matches(Regex.Match(content, "<tbody>(.*?)</tbody>", RegexOptions.Singleline).Groups[1].Value, "<tr>").Count;
    }
}
