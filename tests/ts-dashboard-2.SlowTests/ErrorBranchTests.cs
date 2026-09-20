using TerraScale.Dashboard2.Testing;

namespace TerraScale.Dashboard2.SlowTests;

/// <summary>Error branches: unknown resources, wrong credentials, invalid
/// form input, and malformed flow requests all land on the documented
/// error surfaces instead of 500s.</summary>
public sealed class ErrorBranchTests
{
    [Test]
    public async Task UnknownDatabaseDetailReturns404()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        var page = await app.GetPageAsync(client, "/databases/db_does_not_exist");
        await Assert.That(page.StatusCode).IsEqualTo(404);
    }

    [Test]
    public async Task LoginWithWrongPasswordRedirectsWithError()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();
        using var response = await DashboardTestApp.PostFormAsync(client, "/api/v1/auth/password/login",
            new Dictionary<string, string> { ["email"] = "mario@terrascale.tech", ["password"] = "not-the-password" });
        await Assert.That((int)response.StatusCode).IsEqualTo(302);
        await Assert.That(response.Headers.Location!.ToString())
            .IsEqualTo("/login?error=Incorrect%20email%20or%20password.");
        var page = await app.GetPageAsync(client, "/login?error=Incorrect%20email%20or%20password.");
        await Assert.That(page.Contains("Incorrect email or password.")).IsTrue();
    }

    [Test]
    public async Task InvitationRespondWithBadActionOrUnknownIdShowsError()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"inv-{Guid.NewGuid():N}@example.test");

        using var badAction = await DashboardTestApp.PostFormAsync(client, "/invitations/01J00000000000000000000000/respond",
            new Dictionary<string, string> { ["action"] = "not-a-choice" });
        await Assert.That(badAction.Headers.Location!.ToString())
            .IsEqualTo("/invitations/01J00000000000000000000000?error=This%20invitation%20link%20is%20missing%20its%20organization%20context.");

        using var unknown = await DashboardTestApp.PostFormAsync(client, "/invitations/01JUNKNOWNID0000000000000/respond",
            new Dictionary<string, string> { ["action"] = "accept" });
        await Assert.That(unknown.Headers.Location!.ToString())
            .StartsWith("/invitations/01JUNKNOWNID0000000000000?error=");
    }

    [Test]
    public async Task InvitationAcceptAndDeclineOutcomesRender()
    {
        using var app = new DashboardTestApp();
        var email = $"accept-{Guid.NewGuid():N}@example.test";
        using var client = await app.CreateMemberClientAsync(email);

        using var accepted = await DashboardTestApp.PostFormAsync(client, "/invitations/01J00000000000000000000000/respond",
            new Dictionary<string, string> { ["action"] = "accept" });
        await Assert.That(accepted.Headers.Location!.ToString())
            .IsEqualTo("/invitations/01J00000000000000000000000?outcome=accepted");
        var acceptedPage = await app.GetPageAsync(client, "/invitations/01J00000000000000000000000?outcome=accepted");
        await Assert.That(acceptedPage.Contains("Invitation accepted")).IsTrue();

        using var declined = await DashboardTestApp.PostFormAsync(client, "/invitations/01J00000000000000000000000/respond",
            new Dictionary<string, string> { ["action"] = "decline" });
        await Assert.That(declined.Headers.Location!.ToString())
            .IsEqualTo("/invitations/01J00000000000000000000000?outcome=declined");
        var declinedPage = await app.GetPageAsync(client, "/invitations/01J00000000000000000000000?outcome=declined");
        await Assert.That(declinedPage.Contains("Invitation declined")).IsTrue();
    }

    [Test]
    public async Task ResetPasswordRejectsShortTokenPassword()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();
        using var response = await DashboardTestApp.PostFormAsync(client, "/reset-password",
            new Dictionary<string, string> { ["token"] = "tok_reset_123", ["newPassword"] = "short", ["confirmPassword"] = "short" });
        await Assert.That((int)response.StatusCode).IsEqualTo(302);
        await Assert.That(response.Headers.Location!.ToString())
            .IsEqualTo("/reset-password?token=tok_reset_123&error=Use%20at%20least%2012%20characters.");
        var page = await app.GetPageAsync(client, "/reset-password?token=tok_reset_123&error=Use%20at%20least%2012%20characters.");
        await Assert.That(page.Contains("Use at least 12 characters.")).IsTrue();
    }

    [Test]
    public async Task ResetPasswordRequestAndSetBranches()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();

        using var requested = await DashboardTestApp.PostFormAsync(client, "/reset-password",
            new Dictionary<string, string> { ["email"] = "whoever@example.test" });
        await Assert.That(requested.Headers.Location!.ToString()).IsEqualTo("/reset-password?sent=1");
        var sent = await app.GetPageAsync(client, "/reset-password?sent=1");
        await Assert.That(sent.Contains("Password reset instructions were sent if the account exists.")).IsTrue();

        using var updated = await DashboardTestApp.PostFormAsync(client, "/reset-password",
            new Dictionary<string, string> { ["token"] = "tok_reset_123", ["newPassword"] = "A-brand-new-password-1", ["confirmPassword"] = "A-brand-new-password-1" });
        await Assert.That(updated.Headers.Location!.ToString()).IsEqualTo("/reset-password?token=tok_reset_123&updated=1");
        var done = await app.GetPageAsync(client, "/reset-password?token=tok_reset_123&updated=1");
        await Assert.That(done.Contains("Password updated. You can sign in with your new password.")).IsTrue();
    }

    [Test]
    public async Task OAuthConsentWithoutClientIdShowsError()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        var page = await app.GetPageAsync(client, "/oauth/consent?response_type=code");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Contains("The OAuth authorization request has no valid client.")).IsTrue();
    }

    [Test]
    public async Task OAuthConsentSubmitRecordsApproveAndDeny()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        using var approved = await DashboardTestApp.PostFormAsync(client, "/oauth/consent/submit",
            new Dictionary<string, string> { ["client_id"] = "app_01JCLIENT7Q9W2E4R6T", ["action"] = "approve" });
        await Assert.That(approved.Headers.Location!.ToString())
            .IsEqualTo("/oauth/consent?client_id=app_01JCLIENT7Q9W2E4R6T&outcome=approved");
        var approvedPage = await app.GetPageAsync(client, approved.Headers.Location!.ToString());
        await Assert.That(approvedPage.Contains("Access approved")).IsTrue();

        using var denied = await DashboardTestApp.PostFormAsync(client, "/oauth/consent/submit",
            new Dictionary<string, string> { ["client_id"] = "app_01JCLIENT7Q9W2E4R6T", ["action"] = "deny" });
        var deniedPage = await app.GetPageAsync(client, denied.Headers.Location!.ToString());
        await Assert.That(deniedPage.Contains("Access denied")).IsTrue();
    }

    [Test]
    public async Task AgentApprovalSubmitRecordsApproveAndDeny()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();

        using var approved = await DashboardTestApp.PostFormAsync(client, "/agent/approve/submit",
            new Dictionary<string, string> { ["action"] = "approve" });
        await Assert.That(approved.Headers.Location!.ToString()).IsEqualTo("/agent/approve?outcome=approved");
        var approvedPage = await app.GetPageAsync(client, "/agent/approve?outcome=approved");
        await Assert.That(approvedPage.Contains("Access approved")).IsTrue();

        using var denied = await DashboardTestApp.PostFormAsync(client, "/agent/approve/submit",
            new Dictionary<string, string> { ["action"] = "deny" });
        var deniedPage = await app.GetPageAsync(client, "/agent/approve?outcome=denied");
        await Assert.That(deniedPage.Contains("Access denied")).IsTrue();
    }
}
