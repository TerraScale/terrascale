using System.Text.RegularExpressions;
using TerraScale.Dashboard.Testing;

namespace TerraScale.Dashboard.SlowTests;

/// <summary>Every mutation endpoint round-trips: post the form the slice
/// posts, follow the redirect, and assert the persisted state on the page
/// that renders it. Seeded ids come from the demo world.</summary>
public sealed class MutationsTests
{
    private const string CoreSessionsId = "db_01JC9R3Z6P8T0V4X6Z8B2D4GE";

    [Test]
    public async Task AuditRetentionSettingLimitsPageAndExport()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        using var saved = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/settings/audit-retention",
            new Dictionary<string, string> { ["retentionDays"] = "1" });
        await Assert.That(saved.Headers.Location!.ToString()).IsEqualTo("/settings?tab=audit");

        var settings = await app.GetPageAsync(client, "/settings?tab=audit");
        await Assert.That(settings.Contains("value=\"1\"")).IsTrue();

        var audit = await app.GetPageAsync(client, "/audit?range=90d");
        await Assert.That(audit.Contains("No audit events")).IsTrue();
        await Assert.That(audit.Contains("Showing 0–0 of 0 events")).IsTrue();

        using var export = await client.GetAsync("/audit/export?range=90d&format=json");
        var json = await export.Content.ReadAsStringAsync();
        await Assert.That(json.Contains("\"count\": 0")).IsTrue();

        using var invalid = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/settings/audit-retention",
            new Dictionary<string, string> { ["retentionDays"] = "91" });
        var unchanged = await app.GetPageAsync(client, "/settings?tab=audit");
        await Assert.That(unchanged.Contains("value=\"1\"")).IsTrue();
    }

    // --- Databases ----------------------------------------------------------

    [Test]
    public async Task CreateDatabaseAddsRowToList()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        using var response = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/databases/create",
            new Dictionary<string, string> { ["name"] = "mutation-probe-db" });
        await Assert.That((int)response.StatusCode).IsEqualTo(302);
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/databases");
        var page = await app.GetPageAsync(client, "/databases");
        await Assert.That(page.Contains("mutation-probe-db")).IsTrue();
    }

    [Test]
    public async Task CreateDatabaseWithoutNameRedirectsWithError()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        using var response = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/databases/create",
            new Dictionary<string, string> { ["name"] = "" });
        await Assert.That((int)response.StatusCode).IsEqualTo(302);
        await Assert.That(response.Headers.Location!.ToString())
            .IsEqualTo("/databases?error=Database%20name%20is%20required.");
    }

    [Test]
    public async Task DeleteDatabaseRequiresExactNameConfirmation()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        using var wrongConfirm = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/databases/delete",
            new Dictionary<string, string> { ["databaseId"] = CoreSessionsId, ["confirm"] = "not-the-name" });
        await Assert.That(wrongConfirm.Headers.Location!.ToString()).IsEqualTo($"/databases?delete={CoreSessionsId}");
        var stillThere = await app.GetPageAsync(client, $"/databases?delete={CoreSessionsId}");
        await Assert.That(stillThere.Contains("core-sessions")).IsTrue();
        await Assert.That(stillThere.Contains("Enter the current database name exactly to confirm deletion.")).IsTrue();

        using var deleted = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/databases/delete",
            new Dictionary<string, string> { ["databaseId"] = CoreSessionsId, ["confirm"] = "core-sessions" });
        await Assert.That(deleted.Headers.Location!.ToString()).IsEqualTo("/databases");
        var list = await app.GetPageAsync(client, "/databases");
        await Assert.That(list.Contains("core-sessions")).IsFalse();
        var detail = await app.GetPageAsync(client, $"/databases/{CoreSessionsId}");
        await Assert.That(detail.StatusCode).IsEqualTo(404);
    }

    [Test]
    public async Task DatabaseDetailBackupPolicyToggleTurnsScheduledBackupsOffAndOn()
    {
        const string corePrimaryId = "db_01JC9Q8Y5N7P9R3T5V7X9Z2AC";
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        var initial = await app.GetPageAsync(client, $"/databases/{corePrimaryId}?tab=backups");
        await Assert.That(initial.Contains("aria-checked=\"true\"")).IsTrue();

        using var off = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/databases/backup-policy",
            new Dictionary<string, string> { ["databaseId"] = corePrimaryId, ["enabled"] = "false" });
        await Assert.That(off.Headers.Location!.ToString()).IsEqualTo($"/databases/{corePrimaryId}?tab=backups");
        var disabled = await app.GetPageAsync(client, off.Headers.Location!.ToString());
        await Assert.That(disabled.Contains("aria-checked=\"false\"")).IsTrue();
        await Assert.That(disabled.Contains("href=\"/databases/" + corePrimaryId + "?tab=backups\"")).IsTrue();
        await Assert.That(disabled.Contains("aria-current=\"page\" aria-selected=\"true\"")).IsTrue();

        using var on = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/databases/backup-policy",
            new Dictionary<string, string> { ["databaseId"] = corePrimaryId, ["enabled"] = "true" });
        await Assert.That(on.Headers.Location!.ToString()).IsEqualTo($"/databases/{corePrimaryId}?tab=backups");
        var enabled = await app.GetPageAsync(client, on.Headers.Location!.ToString());
        await Assert.That(enabled.Contains("aria-checked=\"true\"")).IsTrue();
    }

    [Test]
    public async Task DatabaseDetailBranchRequestKeepsBranchesTabAfterRedirect()
    {
        const string corePrimaryId = "db_01JC9Q8Y5N7P9R3T5V7X9Z2AC";
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        using var response = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/databases/branch",
            new Dictionary<string, string> { ["databaseId"] = corePrimaryId });
        await Assert.That(response.Headers.Location!.ToString())
            .IsEqualTo($"/databases/{corePrimaryId}?tab=branches&branched=1");

        var page = await app.GetPageAsync(client, response.Headers.Location!.ToString());
        await Assert.That(page.Contains("Branch request accepted")).IsTrue();
        await Assert.That(page.Contains("href=\"/databases/" + corePrimaryId + "?tab=branches\"")).IsTrue();
        await Assert.That(page.Contains("aria-current=\"page\" aria-selected=\"true\"")).IsTrue();
    }

    [Test]
    public async Task SettingsBackupPolicySavesCronAndClampsRetention()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        using var response = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/settings/backup-policy",
            new Dictionary<string, string>
            {
                ["databaseId"] = CoreSessionsId,
                ["fullBackupCron"] = "30 3 * * *",
                ["incrementalBackupCron"] = "15 */4 * * *",
                ["retentionDays"] = "999",
            });
        await Assert.That(response.Headers.Location!.ToString())
            .IsEqualTo($"/settings?tab=backups&database={CoreSessionsId}");
        var page = await app.GetPageAsync(client, $"/settings?tab=backups&database={CoreSessionsId}");
        await Assert.That(page.Contains("value=\"30 3 * * *\"")).IsTrue();
        await Assert.That(page.Contains("value=\"15 */4 * * *\"")).IsTrue();
        // retentionDays is clamped to the 1..35 window (999 → 35).
        await Assert.That(page.Contains("value=\"35\"")).IsTrue();
    }

    // --- API keys and M2M clients -------------------------------------------

    [Test]
    public async Task CreateApiKeyRevealsSecretOnceThenHidesIt()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        using var response = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/credentials/key",
            new Dictionary<string, string> { ["name"] = "mutation-probe-key", ["scopes"] = "databases:read documents:write" });
        var location = response.Headers.Location!.ToString();
        await Assert.That(location.StartsWith("/api-keys?created=key_", StringComparison.Ordinal)).IsTrue();
        await Assert.That(location.Contains("toast=API%20key%20created.")).IsTrue();

        var reveal = await app.GetPageAsync(client, location);
        await Assert.That(reveal.Contains("Secret is shown once")).IsTrue();
        await Assert.That(reveal.Contains("mutation-probe-key")).IsTrue();
        var secret = Regex.Match(reveal.Html, @"data-copy=""(tsk_live_[0-9a-f]+)""").Groups[1].Value;
        await Assert.That(secret.Length > 0).IsTrue();

        var secondLook = await app.GetPageAsync(client, "/api-keys");
        await Assert.That(secondLook.Contains(secret)).IsFalse();
        await Assert.That(secondLook.Contains("mutation-probe-key")).IsTrue();
    }

    [Test]
    public async Task CreateApiKeyWithoutScopesRedirectsWithError()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        using var response = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/credentials/key",
            new Dictionary<string, string> { ["name"] = "no-scopes" });
        await Assert.That((int)response.StatusCode).IsEqualTo(302);
        await Assert.That(response.Headers.Location!.ToString())
            .IsEqualTo("/api-keys?create=key&error=Enter%20at%20least%20one%20scope.");
        var page = await app.GetPageAsync(client, "/api-keys?create=key&error=Enter%20at%20least%20one%20scope.");
        await Assert.That(page.Contains("Enter at least one scope.")).IsTrue();
    }

    [Test]
    public async Task CreateM2mClientRevealsSecretAndCountsClient()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        using var response = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/credentials/m2m",
            new Dictionary<string, string> { ["name"] = "mutation-probe-m2m", ["scopes"] = "documents:read" });
        var location = response.Headers.Location!.ToString();
        await Assert.That(location.StartsWith("/api-keys?created=m2m_", StringComparison.Ordinal)).IsTrue();
        var page = await app.GetPageAsync(client, location);
        await Assert.That(page.Contains("M2M client created.")).IsTrue();
        await Assert.That(Regex.IsMatch(page.Html, @"data-copy=""tsm_live_[0-9a-f]+""")).IsTrue();
        await Assert.That(page.Contains("mutation-probe-m2m")).IsTrue();
    }

    [Test]
    public async Task RevokeApiKeyMarksItRevokedWithToast()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        using var response = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/credentials/revoke",
            new Dictionary<string, string> { ["kind"] = "key", ["id"] = "key_01JC9Q9Z6N8P1R4T7V0X3Z6B9", ["name"] = "ci-deploy" });
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/api-keys?toast=ci-deploy%20revoked.");
        var page = await app.GetPageAsync(client, "/api-keys?toast=ci-deploy%20revoked.");
        await Assert.That(page.Contains("ci-deploy revoked.")).IsTrue();
        var list = await app.GetPageAsync(client, "/api-keys?status=Revoked");
        await Assert.That(list.Contains("ci-deploy")).IsTrue();
    }

    [Test]
    public async Task RotateCredentialRevealsFreshSecret()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        using var response = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/credentials/rotate",
            new Dictionary<string, string> { ["kind"] = "key", ["id"] = "key_01JC9R4A8P0T3V6X9Z2C5E8HB", ["name"] = "local-dev" });
        await Assert.That(response.Headers.Location!.ToString())
            .IsEqualTo("/api-keys?rotated=1&toast=Credential%20rotated.");
        var page = await app.GetPageAsync(client, "/api-keys?rotated=1&toast=Credential%20rotated.");
        await Assert.That(page.Contains("Credential rotated.")).IsTrue();
        await Assert.That(page.Contains("local-dev")).IsTrue();
    }

    // --- Invitations, members, and roles -------------------------------------

    [Test]
    public async Task AccessInviteSendResendAndRevokeLifecycle()
    {
        var email = $"probe-{Guid.NewGuid():N}@example.test";
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        using var invited = await DashboardTestApp.PostFormAsync(client, "/api/v1/access/invitations",
            new Dictionary<string, string> { ["email"] = email });
        await Assert.That(invited.Headers.Location!.ToString()).IsEqualTo("/access");
        var access = await app.GetPageAsync(client, "/access");
        await Assert.That(access.Contains(email)).IsTrue();

        var invitationId = Regex.Match(access.Html[(access.Html.IndexOf(email, StringComparison.Ordinal))..],
            @"/api/v1/access/invitations/([^/""]+)/revoke").Groups[1].Value;
        await Assert.That(invitationId.Length > 0).IsTrue();

        using var resent = await DashboardTestApp.PostFormAsync(client, $"/api/v1/access/invitations/{invitationId}/resend", new Dictionary<string, string>());
        await Assert.That(resent.Headers.Location!.ToString()).IsEqualTo("/access");

        using var revoked = await DashboardTestApp.PostFormAsync(client, $"/api/v1/access/invitations/{invitationId}/revoke", new Dictionary<string, string>());
        await Assert.That(revoked.Headers.Location!.ToString()).IsEqualTo("/access");
        var organization = await app.GetPageAsync(client, "/organization");
        await Assert.That(organization.Contains(email)).IsTrue();
        await Assert.That(organization.Contains("Revoked")).IsTrue();
    }

    [Test]
    public async Task AccessInviteWithInvalidEmailRedirectsWithError()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        using var response = await DashboardTestApp.PostFormAsync(client, "/api/v1/access/invitations",
            new Dictionary<string, string> { ["email"] = "not-an-email" });
        await Assert.That((int)response.StatusCode).IsEqualTo(302);
        await Assert.That(response.Headers.Location!.ToString())
            .IsEqualTo("/access?error=A%20valid%20invite%20email%20is%20required.");
    }

    [Test]
    public async Task AccessMemberRoleChangeToOwnerFlipsTheMemberBadge()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        var before = await app.GetPageAsync(client, "/access?q=ravi");
        await Assert.That(before.Contains("""ts-badge-success">Organization Owner""")).IsFalse();

        using var changed = await DashboardTestApp.PostFormAsync(client, "/api/v1/access/members/usr_01JC9S9V6Q1X4Z6B8D0F3H6JN/role",
            new Dictionary<string, string> { ["role"] = "owner" });
        await Assert.That(changed.Headers.Location!.ToString()).IsEqualTo("/access");
        var after = await app.GetPageAsync(client, "/access?q=ravi");
        await Assert.That(after.Contains("""ts-badge-success">Organization Owner""")).IsTrue();
    }

    [Test]
    public async Task OrganizationInviteSendResendAndCancelLifecycle()
    {
        var email = $"org-probe-{Guid.NewGuid():N}@example.test";
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        using var invited = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/organization/invitations",
            new Dictionary<string, string> { ["email"] = email });
        await Assert.That(invited.Headers.Location!.ToString()).IsEqualTo("/organization");
        var pending = await app.GetPageAsync(client, "/organization");
        await Assert.That(pending.Contains(email)).IsTrue();
        await Assert.That(pending.Contains("Pending")).IsTrue();

        var invitationId = Regex.Match(pending.Html[(pending.Html.IndexOf(email, StringComparison.Ordinal))..],
            @"name=""invitationId"" value=""([^""]+)""").Groups[1].Value;
        await Assert.That(invitationId.Length > 0).IsTrue();

        using var resent = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/organization/invitations/resend",
            new Dictionary<string, string> { ["invitationId"] = invitationId });
        await Assert.That(resent.Headers.Location!.ToString()).IsEqualTo("/organization");
        var stillPending = await app.GetPageAsync(client, "/organization");
        await Assert.That(stillPending.Contains(email)).IsTrue();

        using var canceled = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/organization/invitations/cancel",
            new Dictionary<string, string> { ["invitationId"] = invitationId });
        await Assert.That(canceled.Headers.Location!.ToString()).IsEqualTo("/organization");
        var canceledPage = await app.GetPageAsync(client, "/organization");
        await Assert.That(canceledPage.Contains(email)).IsTrue();
        await Assert.That(canceledPage.Contains("Revoked")).IsTrue();
    }

    [Test]
    public async Task OrganizationMemberRoleChangeSelectsOwnerOption()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        using var response = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/organization/members/role",
            new Dictionary<string, string> { ["userId"] = "usr_01JC9S9V6Q1X4Z6B8D0F3H6JN", ["role"] = "owner" });
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/organization");
        var page = await app.GetPageAsync(client, "/organization");
        await Assert.That(page.Contains("value=\"owner\" selected")).IsTrue();
    }

    // --- Organization and projects -------------------------------------------

    [Test]
    public async Task RenameOrganizationUpdatesDetailsPanel()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        using var response = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/organization/rename",
            new Dictionary<string, string> { ["name"] = "Renamed Labs" });
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/organization");
        var page = await app.GetPageAsync(client, "/organization");
        await Assert.That(page.Contains("Renamed Labs")).IsTrue();
        await Assert.That(page.Contains("TerraScale Labs")).IsFalse();
    }

    [Test]
    public async Task CreateProjectAddsRowAndSwitchProjectChangesFleet()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        using var created = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/organization/projects",
            new Dictionary<string, string> { ["name"] = "probe-project", ["regions"] = "GRU" });
        await Assert.That(created.Headers.Location!.ToString()).IsEqualTo("/organization");
        var organization = await app.GetPageAsync(client, "/organization");
        await Assert.That(organization.Contains("probe-project")).IsTrue();

        using var switched = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/switch-project",
            new Dictionary<string, string> { ["projectId"] = "prj_01JC9R2X4P6T8V1Z3B5D7F9HC", ["returnUrl"] = "/databases" });
        await Assert.That(switched.Headers.Location!.ToString()).IsEqualTo("/databases");
        var databases = await app.GetPageAsync(client, "/databases");
        await Assert.That(databases.Contains("edge-events")).IsTrue();
        await Assert.That(databases.Contains("core-primary")).IsFalse();
    }

    [Test]
    public async Task RenameProjectUpdatesRowAndPlacement()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        using var response = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/organization/projects/rename",
            new Dictionary<string, string>
            {
                ["projectId"] = "prj_01JC9R2X4P6T8V1Z3B5D7F9HC",
                ["name"] = "edge-renamed",
                ["backupRegion"] = "FRA",
                ["regions"] = "FRA",
            });
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/organization");
        var page = await app.GetPageAsync(client, "/organization");
        await Assert.That(page.Contains("edge-renamed")).IsTrue();
        await Assert.That(page.Contains("edge-analytics")).IsFalse();
        await Assert.That(page.Contains(">FRA<")).IsTrue();
    }

    // --- Profile --------------------------------------------------------------

    [Test]
    public async Task ProfileSaveUpdatesNameAndPhone()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        using var response = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/profile",
            new Dictionary<string, string> { ["name"] = "Probe User", ["phoneNumber"] = "55 11 91234-5678" });
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/profile");
        var page = await app.GetPageAsync(client, "/profile");
        await Assert.That(page.Contains("Probe User")).IsTrue();
        await Assert.That(page.Contains("value=\"55 11 91234-5678\"")).IsTrue();
    }

    [Test]
    public async Task PasswordChangeValidatesCurrentLengthAndMatch()
    {
        using var app = new DashboardTestApp();
        var email = $"pw-{Guid.NewGuid():N}@example.test";
        using var client = await app.CreateMemberClientAsync(email);

        using var wrongCurrent = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/profile/password",
            new Dictionary<string, string> { ["currentPassword"] = "wrong", ["newPassword"] = "A-brand-new-password-1", ["confirmPassword"] = "A-brand-new-password-1" });
        await Assert.That(wrongCurrent.Headers.Location!.ToString())
            .IsEqualTo("/profile?tab=security&error=Current%20password%20is%20incorrect.");

        using var tooShort = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/profile/password",
            new Dictionary<string, string> { ["currentPassword"] = "Member-tests-123!", ["newPassword"] = "short", ["confirmPassword"] = "short" });
        await Assert.That(tooShort.Headers.Location!.ToString())
            .IsEqualTo("/profile?tab=security&error=Use%20at%20least%2012%20characters.");

        using var mismatch = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/profile/password",
            new Dictionary<string, string> { ["currentPassword"] = "Member-tests-123!", ["newPassword"] = "A-brand-new-password-1", ["confirmPassword"] = "different-password" });
        await Assert.That(mismatch.Headers.Location!.ToString())
            .IsEqualTo("/profile?tab=security&error=Passwords%20do%20not%20match.");
    }

    [Test]
    public async Task PasswordChangeSignsOutAndAcceptsTheNewPassword()
    {
        using var app = new DashboardTestApp();
        var email = $"pw-{Guid.NewGuid():N}@example.test";
        using var client = await app.CreateMemberClientAsync(email);
        using var changed = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/profile/password",
            new Dictionary<string, string> { ["currentPassword"] = "Member-tests-123!", ["newPassword"] = "A-brand-new-password-1", ["confirmPassword"] = "A-brand-new-password-1" });
        await Assert.That(changed.Headers.Location!.ToString()).IsEqualTo("/login");

        using var signedOut = await client.GetAsync("/");
        await Assert.That((int)signedOut.StatusCode).IsEqualTo(302);
        await Assert.That(signedOut.Headers.Location!.ToString()).IsEqualTo("/login");

        using var fresh = app.CreateClient();
        await DashboardTestApp.PostFormAsync(fresh, "/api/v1/auth/password/login",
            new Dictionary<string, string> { ["email"] = email, ["password"] = "A-brand-new-password-1" });
        var page = await app.GetPageAsync(fresh, "/");
        await Assert.That(page.Heading).IsEqualTo("Overview");
    }

    // --- Settings and usage ------------------------------------------------------

    [Test]
    public async Task SettingsAuthenticationToggleTurnsOffAndOn()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/settings/authentication", new Dictionary<string, string>());
        var off = await app.GetPageAsync(client, "/settings?tab=identity");
        await Assert.That(off.Contains("checked onchange")).IsFalse();

        using var on = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/settings/authentication",
            new Dictionary<string, string> { ["enabled"] = "true" });
        await Assert.That(on.Headers.Location!.ToString()).IsEqualTo("/settings?tab=identity");
        var enabled = await app.GetPageAsync(client, "/settings?tab=identity");
        await Assert.That(enabled.Contains("checked onchange")).IsTrue();
    }

    [Test]
    public async Task UsageSpendingControlsSaveWithCapAndRejectInvalidAmounts()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        using var invalid = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/usage/spending-controls",
            new Dictionary<string, string> { ["paidExcess"] = "true", ["spendCap"] = "-5" });
        await Assert.That(invalid.Headers.Location!.ToString()).IsEqualTo("/usage?tab=settings&saved=invalid");
        var invalidPage = await app.GetPageAsync(client, "/usage?tab=settings&saved=invalid");
        await Assert.That(invalidPage.Contains("Enter a non-negative dollar amount with at most two decimal places.")).IsTrue();

        using var saved = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/usage/spending-controls",
            new Dictionary<string, string> { ["paidExcess"] = "true", ["spendCap"] = "100.50" });
        await Assert.That(saved.Headers.Location!.ToString()).IsEqualTo("/usage?tab=settings&saved=ok");
        var page = await app.GetPageAsync(client, "/usage?tab=settings&saved=ok");
        await Assert.That(page.Contains("Spending controls saved.")).IsTrue();
        await Assert.That(page.Contains("value=\"100.50\"")).IsTrue();

        // Caps form posts no spendCap: project/database caps save, organization cap stays.
        using var caps = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/usage/spending-controls",
            new Dictionary<string, string> { ["projCap_prj_01JC9Q6W3M5N7P9R2T4V6X8ZA"] = "80", ["dbCap_db_01JC9Q8Y5N7P9R3T5V7X9Z2AC"] = "90" });
        await Assert.That(caps.Headers.Location!.ToString()).IsEqualTo("/usage?tab=settings&saved=ok");
        var capsPage = await app.GetPageAsync(client, "/usage?tab=settings&saved=ok");
        await Assert.That(capsPage.Contains("value=\"100.50\"")).IsTrue();
        await Assert.That(capsPage.Contains("name=\"projCap_prj_01JC9Q6W3M5N7P9R2T4V6X8ZA\" inputmode=\"decimal\" pattern=\"\\d+(\\.\\d{1,2})?\" value=\"80\"")).IsTrue();
        // The looser database cap loses to its project cap.
        await Assert.That(capsPage.Contains("id=\"dbCap_db_01JC9Q8Y5N7P9R3T5V7X9Z2AC_applied\" class=\"ts-muted\">$80 ")).IsTrue();
    }

    // --- Saved queries -------------------------------------------------------------

    [Test]
    public async Task SavedQueryCreateErrorsWithoutNameOrQuery()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        using var response = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/data/saved-queries",
            new Dictionary<string, string> { ["name"] = "", ["query"] = "" });
        await Assert.That(response.Headers.Location!.ToString())
            .IsEqualTo("/data?view=queries&error=A%20name%20and%20SQL%20query%20are%20required.");
        var page = await app.GetPageAsync(client, "/data?view=queries&error=A%20name%20and%20SQL%20query%20are%20required.");
        await Assert.That(page.Contains("A name and SQL query are required.")).IsTrue();
    }

    [Test]
    public async Task SavedQueryCreateAndDeleteRoundTrip()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        using var created = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/data/saved-queries",
            new Dictionary<string, string> { ["name"] = "probe-query", ["query"] = "SELECT * FROM orders WHERE total > 900", ["view"] = "queries" });
        await Assert.That(created.Headers.Location!.ToString()).IsEqualTo("/data?view=queries");
        var page = await app.GetPageAsync(client, "/data?view=queries");
        await Assert.That(page.Contains("probe-query")).IsTrue();
        await Assert.That(page.Contains("SELECT * FROM orders WHERE total &gt; 900")).IsTrue();

        var savedQueryId = Regex.Match(page.Html[(page.Html.IndexOf("probe-query", StringComparison.Ordinal))..],
            @"delete=(sq_[^&""]+)").Groups[1].Value;
        await Assert.That(savedQueryId.Length > 0).IsTrue();
        using var deleted = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/data/saved-queries/delete",
            new Dictionary<string, string> { ["savedQueryId"] = savedQueryId, ["view"] = "queries" });
        await Assert.That(deleted.Headers.Location!.ToString()).IsEqualTo("/data?view=queries");
        var after = await app.GetPageAsync(client, "/data?view=queries");
        await Assert.That(after.Contains("probe-query")).IsFalse();
    }

    // --- Notifications -------------------------------------------------------------

    [Test]
    public async Task MarkOneNotificationReadRemovesItsMarkReadForm()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        const string notificationId = "ntf_01JC9UBD4U6W8Y0B3D5F7J2NP";

        var before = await app.GetPageAsync(client, "/notifications");
        await Assert.That(before.Contains($"/api/v1/notifications/{notificationId}/read")).IsTrue();

        using var response = await DashboardTestApp.PostFormAsync(client, $"/api/v1/notifications/{notificationId}/read", new Dictionary<string, string>());
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/notifications");
        var after = await app.GetPageAsync(client, "/notifications");
        await Assert.That(after.Contains($"/api/v1/notifications/{notificationId}/read")).IsFalse();
        await Assert.That(after.Contains("API key expiring soon")).IsTrue();
    }

    [Test]
    public async Task MarkAllNotificationsReadDisablesTheBulkButton()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        using var response = await DashboardTestApp.PostFormAsync(client, "/api/v1/notifications/read-all", new Dictionary<string, string>());
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/notifications");
        var page = await app.GetPageAsync(client, "/notifications");
        await Assert.That(page.Contains("disabled=\"disabled\"")).IsTrue();
        await Assert.That(page.Contains("API key expiring soon")).IsTrue();
    }
}
