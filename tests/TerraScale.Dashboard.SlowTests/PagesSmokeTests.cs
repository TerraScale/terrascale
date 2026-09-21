using TerraScale.Dashboard.Testing;

namespace TerraScale.Dashboard.SlowTests;

/// <summary>Every page route renders 200 with its real copy: the demo
/// account's seeded member pages, the public flows, and a freshly registered
/// member's degraded (empty) states.</summary>
public sealed class PagesSmokeTests
{
    [Test]
    public async Task OverviewPageRendersWorkspaceSummary()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        var page = await app.GetPageAsync(client, "/");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Overview");
        await Assert.That(page.Contains("Workspace metrics")).IsTrue();
        await Assert.That(page.Contains("Getting started")).IsTrue();
        await Assert.That(page.Contains("Connect your AI or platform")).IsTrue();
    }

    [Test]
    public async Task DatabasesPageRendersSeededFleet()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        var page = await app.GetPageAsync(client, "/databases");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Databases");
        await Assert.That(page.Contains("Logical databases")).IsTrue();
        await Assert.That(page.Contains("core-primary")).IsTrue();
        await Assert.That(page.Contains("core-sessions")).IsTrue();
    }

    [Test]
    public async Task DatabaseDetailPageRendersPlacementBackupsAndBranches()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        var page = await app.GetPageAsync(client, "/databases/db_01JC9Q8Y5N7P9R3T5V7X9Z2AC");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Contains("core-primary")).IsTrue();
        await Assert.That(page.Contains("Placement and latency")).IsTrue();
        await Assert.That(page.Contains("Backups")).IsTrue();
        await Assert.That(page.Contains("Branches")).IsTrue();
    }

    [Test]
    public async Task DataPageRendersSavedQueriesView()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        var page = await app.GetPageAsync(client, "/data");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Tables");
        await Assert.That(page.Contains("Saved queries")).IsTrue();
        await Assert.That(page.Contains("orders-over-500")).IsTrue();
        await Assert.That(page.Contains("Collection documents")).IsTrue();
        await Assert.That(page.Contains("SQL console")).IsTrue();
    }

    [Test]
    public async Task SchemaPageRendersSeededCollections()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        var page = await app.GetPageAsync(client, "/schema");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Schema");
        await Assert.That(page.Contains("Schema viewer")).IsTrue();
        await Assert.That(page.Contains("orders")).IsTrue();
        await Assert.That(page.Contains("sessions")).IsTrue();
    }

    [Test]
    public async Task IndexesPageRendersSeededIndexes()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        var page = await app.GetPageAsync(client, "/indexes");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Indexes");
        await Assert.That(page.Contains("Query performance and indexes")).IsTrue();
        await Assert.That(page.Contains("orders_id_idx")).IsTrue();
    }

    [Test]
    public async Task HistoryPageRendersScopeAndRequiredParamsNotice()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        var page = await app.GetPageAsync(client, "/history");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Document history");
        await Assert.That(page.Contains("Inspect causal versions and understand deterministic conflict resolution.")).IsTrue();
        await Assert.That(page.Contains("A database, collection, and document are required to view history.")).IsTrue();

        var complete = await app.GetPageAsync(client, "/history?database=d&collection=c&document=x");
        await Assert.That(complete.Contains("Document history is temporarily unavailable.")).IsTrue();
    }

    [Test]
    public async Task ApiKeysPageRendersSeededCredentials()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        var page = await app.GetPageAsync(client, "/api-keys");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("API keys");
        await Assert.That(page.Contains("Credentials")).IsTrue();
        await Assert.That(page.Contains("ci-deploy")).IsTrue();
        await Assert.That(page.Contains("ingest-worker")).IsTrue();
    }

    [Test]
    public async Task AccessPageRendersMembersSessionsAndInvitations()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        var page = await app.GetPageAsync(client, "/access");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Team");
        await Assert.That(page.Contains("Organization members")).IsTrue();
        await Assert.That(page.Contains("Current session access")).IsTrue();
        await Assert.That(page.Contains("Invitations and role changes")).IsTrue();
        await Assert.That(page.Contains("Ana Souza")).IsTrue();
    }

    [Test]
    public async Task AuditPageRendersActivityAndBreakdowns()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        var page = await app.GetPageAsync(client, "/audit");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Logs");
        await Assert.That(page.Contains("Recent activity")).IsTrue();
        await Assert.That(page.Contains("Retention")).IsTrue();
        await Assert.That(page.Contains("Severity breakdown")).IsTrue();
    }

    [Test]
    public async Task NotificationsPageRendersSeededInbox()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        var page = await app.GetPageAsync(client, "/notifications");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Notifications");
        await Assert.That(page.Contains("Inbox")).IsTrue();
        await Assert.That(page.Contains("API key expiring soon")).IsTrue();
        await Assert.That(page.Contains("Mark all read")).IsTrue();
    }

    [Test]
    public async Task OrganizationPageRendersProjectsDetailsMembersAndInvitations()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        var page = await app.GetPageAsync(client, "/organization");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Organization");
        await Assert.That(page.Contains("TerraScale Labs")).IsTrue();
        await Assert.That(page.Contains("core-platform")).IsTrue();
        await Assert.That(page.Contains("Members")).IsTrue();
        await Assert.That(page.Contains("Invitations")).IsTrue();
    }

    [Test]
    public async Task OrganizationsNewPageRendersWizard()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        var page = await app.GetPageAsync(client, "/organizations/new");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Create organization");
        await Assert.That(page.Contains("Repeat only the onboarding steps a new tenant needs: organization, payment, and database.")).IsTrue();
        await Assert.That(page.Contains("Organization name")).IsTrue();
    }

    [Test]
    public async Task ProfilePageRendersTabsAndAccount()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        var page = await app.GetPageAsync(client, "/profile");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Profile");
        await Assert.That(page.Contains("Mario Kraus")).IsTrue();
        await Assert.That(page.Contains("Email and contact")).IsTrue();
        await Assert.That(page.Contains("Change password")).IsFalse();

        var security = await app.GetPageAsync(client, "/profile?tab=security");
        await Assert.That(security.StatusCode).IsEqualTo(200);
        await Assert.That(security.Contains("Change password")).IsTrue();
        await Assert.That(security.Contains("Multi-factor authentication")).IsTrue();

        var sessions = await app.GetPageAsync(client, "/profile?tab=sessions");
        await Assert.That(sessions.StatusCode).IsEqualTo(200);
        await Assert.That(sessions.Contains("Active sessions")).IsTrue();
        await Assert.That(sessions.Contains("203.0.113.24")).IsTrue();
        await Assert.That(sessions.Contains("ts-cli")).IsTrue();
    }

    [Test]
    public async Task SettingsPageRendersEveryTab()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        var identity = await app.GetPageAsync(client, "/settings");
        await Assert.That(identity.StatusCode).IsEqualTo(200);
        await Assert.That(identity.Heading).IsEqualTo("Settings");
        await Assert.That(identity.Contains("Authentication product")).IsTrue();
        await Assert.That(identity.Contains("Microsoft Entra ID")).IsTrue();

        var credentials = await app.GetPageAsync(client, "/settings?tab=credentials");
        await Assert.That(credentials.StatusCode).IsEqualTo(200);
        await Assert.That(credentials.Contains("Project credentials")).IsTrue();

        var webhooks = await app.GetPageAsync(client, "/settings?tab=webhooks");
        await Assert.That(webhooks.StatusCode).IsEqualTo(200);
        await Assert.That(webhooks.Contains("No webhooks")).IsTrue();

        var backups = await app.GetPageAsync(client, "/settings?tab=backups");
        await Assert.That(backups.StatusCode).IsEqualTo(200);
        await Assert.That(backups.Contains("Backup policy")).IsTrue();
        await Assert.That(backups.Contains("core-primary")).IsTrue();

        var danger = await app.GetPageAsync(client, "/settings?tab=danger");
        await Assert.That(danger.StatusCode).IsEqualTo(200);
        await Assert.That(danger.Contains("Danger zone")).IsTrue();
    }

    [Test]
    public async Task AuthenticationConsoleRendersEveryManagementSurface()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        var pages = new (string Path, string Copy)[]
        {
            ("/auth", "Your application's identity workspace."),
            ("/auth/users", "Manage your application's users, profiles and access."),
            ("/auth/sessions", "Inspect and revoke application sessions."),
            ("/auth/organizations", "Group application users into customer organizations."),
            ("/auth/memberships", "Assign users to organizations with an explicit role."),
            ("/auth/invitations", "Prepare invitations to your application organizations."),
            ("/auth/domains", "Configure organization enrollment by email domain."),
            ("/auth/connections", "Configure social and enterprise sign-in with OpenID Connect or SAML."),
            ("/auth/directories", "Prepare SCIM 2.0 endpoints for workforce provisioning."),
            ("/auth/clients", "Register web apps, public clients and service integrations."),
            ("/auth/templates", "Define custom claims for tokens minted by ts-auth."),
            ("/auth/keys", "Issue scoped credentials for the ts-auth management API."),
            ("/auth/webhooks", "Configure signed identity event delivery to your application."),
            ("/auth/deliveries", "Inspect delivery outcomes and queue failed events for replay."),
            ("/auth/audit", "Review changes made to application identities and configuration."),
            ("/auth/billing", "Review identity usage and your instance plan."),
            ("/auth/quickstart", "Connect your application using the ts-auth management API."),
            ("/auth/settings", "Instance branding and hosted sign-in defaults."),
        };

        foreach (var (path, copy) in pages)
        {
            var page = await app.GetPageAsync(client, path);
            await Assert.That(page.StatusCode).IsEqualTo(200);
            await Assert.That(page.Heading).IsEqualTo("Authentication");
            await Assert.That(page.Contains(copy)).IsTrue();
        }
    }

    [Test]
    public async Task SubscriptionsPageRendersSharedViewPanel()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        var page = await app.GetPageAsync(client, "/subscriptions");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Subscriptions");
        await Assert.That(page.Contains("Live shared view")).IsTrue();
    }

    [Test]
    public async Task UsagePageRendersStatsAndSpendingControls()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        var page = await app.GetPageAsync(client, "/usage");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Usage");
        await Assert.That(page.Contains("Save spending controls")).IsTrue();
        await Assert.That(page.Contains("Monthly organization spending cap in USD")).IsTrue();
    }

    [Test]
    public async Task EnginePageRendersNotConfiguredPanel()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        var page = await app.GetPageAsync(client, "/engine");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Engine observability is not configured");
    }

    [Test]
    public async Task PublicLoginPageRendersSignInForm()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();
        var page = await app.GetPageAsync(client, "/login");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Sign in");
        await Assert.That(page.Contains("Access your databases and cloud operations.")).IsTrue();
        await Assert.That(page.Contains("Or continue with")).IsTrue();
    }

    [Test]
    public async Task PublicResetPasswordPageRendersRequestAndTokenModes()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();

        var request = await app.GetPageAsync(client, "/reset-password");
        await Assert.That(request.StatusCode).IsEqualTo(200);
        await Assert.That(request.Heading).IsEqualTo("Reset password");
        await Assert.That(request.Contains("We will send password reset instructions to your email.")).IsTrue();

        var token = await app.GetPageAsync(client, "/reset-password?token=tok_reset_123");
        await Assert.That(token.StatusCode).IsEqualTo(200);
        await Assert.That(token.Heading).IsEqualTo("Set a new password");
        await Assert.That(token.Contains("Choose a new password for your TerraScale account.")).IsTrue();
    }

    [Test]
    public async Task PublicOnboardingPageRendersRegistrationForm()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();
        var page = await app.GetPageAsync(client, "/onboarding");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Activate your TerraScale account");
        await Assert.That(page.Contains("I accept the Terms of Service")).IsTrue();
        await Assert.That(page.Contains("ACTIVATION STEP 1 OF 5")).IsTrue();
    }

    [Test]
    public async Task PublicCheckoutPageRendersLocalPaymentSetup()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();
        var page = await app.GetPageAsync(client, "/checkout/cs_local_01JCHECKOUT7F3K9Q");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("TerraScale local payment setup");
        await Assert.That(page.Contains("Use local test payment method")).IsTrue();
        await Assert.That(page.Title).IsEqualTo("Checkout | TerraScale · TerraScale");
    }

    [Test]
    public async Task PublicInvitationPageRendersAcceptDeclineChoice()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();
        var page = await app.GetPageAsync(client, "/invitations/01J00000000000000000000000");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Organization invitation");
        await Assert.That(page.Contains("Accept to join as a Member.")).IsTrue();
        await Assert.That(page.Contains("Accept invitation")).IsTrue();
        await Assert.That(page.Contains("Decline")).IsTrue();
    }

    [Test]
    public async Task PublicAgentApprovePageRendersRequestDetails()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();
        var page = await app.GetPageAsync(client, "/agent/approve");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Approve agent access");
        await Assert.That(page.Contains("Agent requesting access")).IsTrue();
        await Assert.That(page.Contains("Request details")).IsTrue();
    }

    [Test]
    public async Task OAuthConsentPageRendersDemoClientForSignedInUser()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        var page = await app.GetPageAsync(client, "/oauth/consent?client_id=app_01JCLIENT7Q9W2E4R6T");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Authorize application");
        await Assert.That(page.Contains("Northwind Deploy")).IsTrue();
        await Assert.That(page.Contains("app_01JCLIENT7Q9W2E4R6T")).IsTrue();
        await Assert.That(page.Contains("openid")).IsTrue();
    }

    [Test]
    public async Task BillingPortalPageRendersForSignedInUser()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        var page = await app.GetPageAsync(client, "/billing/portal/cs_local_01JCHECKOUT7F3K9Q");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("TerraScale local billing portal");
    }

    [Test]
    public async Task FreshMemberSeesDegradedAndEmptyStates()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"smoke-{Guid.NewGuid():N}@example.test");

        var overview = await app.GetPageAsync(client, "/");
        await Assert.That(overview.Contains("Monitoring snapshot may be stale")).IsTrue();
        await Assert.That(overview.Contains("No activity recorded in this range yet.")).IsTrue();

        var databases = await app.GetPageAsync(client, "/databases");
        await Assert.That(databases.Contains("No databases saved")).IsTrue();

        var data = await app.GetPageAsync(client, "/data");
        await Assert.That(data.Contains("No saved queries")).IsTrue();

        var audit = await app.GetPageAsync(client, "/audit");
        await Assert.That(audit.Contains("No audit events")).IsTrue();
    }
}
