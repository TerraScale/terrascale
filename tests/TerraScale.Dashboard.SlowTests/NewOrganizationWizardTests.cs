using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.SlowTests;

/// <summary>
/// The Dashboard "create another organization" modal: it repeats only the three
/// onboarding screens a new tenant needs (organization, payment, project),
/// reusing the activation wizard's presentation. The tenant it creates is a
/// paid Business organization, so the payment screen has no bypass and the
/// first project only lands once billing is active.
/// </summary>
public sealed class NewOrganizationWizardTests
{
    private const string StepAction = "/api/v1/dashboard/organization/new-organization";

    /// <summary>Creates the pending Business tenant, pays for it, and returns
    /// the modal's project-step URL.</summary>
    private static async Task<string> ReachProjectStepAsync(HttpClient client, string? plan = null)
    {
        using var organization = await DashboardTestApp.PostFormAsync(client, StepAction,
            new Dictionary<string, string> { ["step"] = "organization", ["name"] = "Nova Labs" });
        var paymentFields = new Dictionary<string, string> { ["step"] = "payment-method" };
        if (plan is not null) paymentFields["plan"] = plan;
        using var payment = await DashboardTestApp.PostFormAsync(client, StepAction, paymentFields);
        var checkout = payment.Headers.Location!.ToString();
        using var completed = await DashboardTestApp.PostFormAsync(client, checkout + "/complete",
            new Dictionary<string, string>());
        return completed.Headers.Location!.ToString();
    }

    [Test]
    public async Task OrganizationPageRendersTheModalWithTheOnboardingScreens()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        var page = await app.GetPageAsync(client, "/organization");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Contains("data-open-dialog=\"create-organization\"")).IsTrue();
        await Assert.That(page.Contains("id=\"create-organization\"")).IsTrue();
        await Assert.That(page.Contains("STEP 1 OF 3")).IsTrue();
        await Assert.That(page.Contains("ts-onboarding-section")).IsTrue();
        await Assert.That(page.Contains("ts-onboarding-step")).IsTrue();
        await Assert.That(page.Contains("Organization name")).IsTrue();
    }

    [Test]
    public async Task OrganizationStepReopensTheModalOnPayment()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        using var response = await DashboardTestApp.PostFormAsync(client, StepAction,
            new Dictionary<string, string> { ["step"] = "organization", ["name"] = "Nova Labs" });
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/organization?newOrganization=payment");

        var page = await app.GetPageAsync(client, "/organization?newOrganization=payment");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Contains("data-autoopen")).IsTrue();
        await Assert.That(page.Contains("STEP 2 OF 3")).IsTrue();
        await Assert.That(page.Contains("Register a payment method")).IsTrue();
        await Assert.That(page.Contains("Starter")).IsTrue();
        // A Business organization always carries its own paid billing, so the
        // onboarding screen's bypass button (rendered only behind AllowBypass)
        // is not offered here; the phrase never appears in the /organization
        // markup.
        await Assert.That(page.Contains("Continue without payment")).IsFalse();
        await Assert.That(page.Contains("Continue to secure payment setup")).IsTrue();
    }

    [Test]
    public async Task InvalidOrganizationNameReopensTheFirstScreenWithTheError()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        using var response = await DashboardTestApp.PostFormAsync(client, StepAction,
            new Dictionary<string, string> { ["step"] = "organization", ["name"] = "" });
        await Assert.That(response.Headers.Location!.ToString())
            .StartsWith("/organization?newOrganization=organization&error=");

        var page = await app.GetPageAsync(client, response.Headers.Location!.ToString());
        await Assert.That(page.Contains("data-autoopen")).IsTrue();
        await Assert.That(page.Contains("STEP 1 OF 3")).IsTrue();
        await Assert.That(page.Contains("Choose a name and an available paid plan.")).IsTrue();
    }

    [Test]
    public async Task SecurePaymentOpensCheckoutThatResumesTheModal()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        using var organization = await DashboardTestApp.PostFormAsync(client, StepAction,
            new Dictionary<string, string> { ["step"] = "organization", ["name"] = "Nova Labs" });
        await Assert.That(organization.Headers.Location!.ToString()).IsEqualTo("/organization?newOrganization=payment");

        using var response = await DashboardTestApp.PostFormAsync(client, StepAction,
            new Dictionary<string, string> { ["step"] = "payment-method" });
        var checkout = response.Headers.Location!.ToString();
        await Assert.That(checkout).StartsWith("/checkout/cs_local_");

        using var completed = await DashboardTestApp.PostFormAsync(client, checkout + "/complete",
            new Dictionary<string, string>());
        await Assert.That(completed.Headers.Location!.ToString())
            .IsEqualTo("/organization?newOrganization=project&payment=success");

        var resumed = await app.GetPageAsync(client, "/organization?newOrganization=project&payment=success");
        await Assert.That(resumed.Contains("data-autoopen")).IsTrue();
        await Assert.That(resumed.Contains("STEP 3 OF 3")).IsTrue();
        await Assert.That(resumed.Contains("Create your first project")).IsTrue();
    }

    [Test]
    public async Task CanceledCheckoutReopensThePaymentScreenWithTheSavedProgressBanner()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        using var organization = await DashboardTestApp.PostFormAsync(client, StepAction,
            new Dictionary<string, string> { ["step"] = "organization", ["name"] = "Nova Labs" });
        using var response = await DashboardTestApp.PostFormAsync(client, StepAction,
            new Dictionary<string, string> { ["step"] = "payment-method" });
        var checkout = response.Headers.Location!.ToString();

        using var canceled = await DashboardTestApp.PostFormAsync(client, checkout + "/cancel",
            new Dictionary<string, string>());
        await Assert.That(canceled.Headers.Location!.ToString())
            .IsEqualTo("/organization?newOrganization=project&payment=canceled");

        // The payment result overrides the URL's project step.
        var page = await app.GetPageAsync(client, "/organization?newOrganization=project&payment=canceled");
        await Assert.That(page.Contains("data-autoopen")).IsTrue();
        await Assert.That(page.Contains("STEP 2 OF 3")).IsTrue();
        await Assert.That(page.Contains("Payment setup canceled")).IsTrue();
        await Assert.That(page.Contains("Register a payment method")).IsTrue();
    }

    [Test]
    public async Task UnknownStepReturnsToTheFirstScreen()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        using var response = await DashboardTestApp.PostFormAsync(client, StepAction,
            new Dictionary<string, string> { ["step"] = "not-a-step" });
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/organization?newOrganization=organization");
    }

    [Test]
    public async Task PaymentStepBeforeOrganizationCreationReturnsToTheFirstScreen()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        // Without a pending Business organization the payment step must not run
        // checkout against whatever workspace happens to be active (here the
        // demo client's already-billed "TerraScale Labs" Business org).
        using var response = await DashboardTestApp.PostFormAsync(client, StepAction,
            new Dictionary<string, string> { ["step"] = "payment-method" });
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/organization?newOrganization=organization");
    }

    [Test]
    public async Task ProjectStepRejectsThePersonalFreeAllowanceWorkspace()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"neworg-personal-{Guid.NewGuid():N}@example.test");

        // The Personal workspace satisfies CanProvisionResources, but the
        // wizard's project step must only land in a billing-active Business
        // tenant.
        using var response = await DashboardTestApp.PostFormAsync(client, StepAction,
            new Dictionary<string, string> { ["step"] = "project", ["name"] = "nova-prod", ["region"] = "GRU" });
        await Assert.That(response.Headers.Location!.ToString())
            .StartsWith("/organization?newOrganization=payment&error=");

        var page = await app.GetPageAsync(client, "/organization");
        await Assert.That(page.Contains("nova-prod")).IsFalse();
    }

    [Test]
    public async Task StaleCheckoutReturnFallsBackToTheFirstStepWithoutBilling()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"neworg-stale-{Guid.NewGuid():N}@example.test");

        // A replayed checkout return with no billed Business tenant must not
        // render the project step as if payment had succeeded.
        var page = await app.GetPageAsync(client, "/organization?newOrganization=project&payment=success");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Contains("data-autoopen")).IsTrue();
        await Assert.That(page.Contains("STEP 1 OF 3")).IsTrue();
        await Assert.That(page.Contains("Create your first project")).IsFalse();
    }

    [Test]
    public async Task RepeatedOrganizationSubmissionResumesTheSamePendingTenant()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"neworg-resume-{Guid.NewGuid():N}@example.test");

        using var first = await DashboardTestApp.PostFormAsync(client, StepAction,
            new Dictionary<string, string> { ["step"] = "organization", ["name"] = "Nova Labs" });
        using var second = await DashboardTestApp.PostFormAsync(client, StepAction,
            new Dictionary<string, string> { ["step"] = "organization", ["name"] = "Nova Labs" });
        await Assert.That(second.Headers.Location!.ToString()).IsEqualTo("/organization?newOrganization=payment");

        var page = await app.GetPageAsync(client, "/organization");
        // The switcher lists every organization, so a resumed submission that
        // created a duplicate tenant would show two "Nova Labs (Business)" rows.
        await Assert.That(Regex.Matches(page.Html, "Nova Labs \\(Business\\)").Count).IsEqualTo(1);

        // The plan chosen after the resume still bills the matching store plan.
        using var payment = await DashboardTestApp.PostFormAsync(client, StepAction,
            new Dictionary<string, string> { ["step"] = "payment-method", ["plan"] = "business" });
        using var completed = await DashboardTestApp.PostFormAsync(client, payment.Headers.Location!.ToString() + "/complete",
            new Dictionary<string, string>());
        using var project = await DashboardTestApp.PostFormAsync(client, StepAction,
            new Dictionary<string, string> { ["step"] = "project", ["name"] = "nova-prod", ["region"] = "GRU" });

        var organization = await app.GetPageAsync(client, "/organization");
        await Assert.That(organization.Contains("Nova Labs")).IsTrue();
        var usage = await app.GetPageAsync(client, "/usage?tab=billing");
        await Assert.That(usage.Contains("<span class=\"ts-kpi-value\">Dedicated</span>")).IsTrue();
    }

    [Test]
    public async Task EmptyProjectNameReopensTheProjectScreenWithTheError()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"neworg-empty-{Guid.NewGuid():N}@example.test");
        await ReachProjectStepAsync(client);

        using var response = await DashboardTestApp.PostFormAsync(client, StepAction,
            new Dictionary<string, string> { ["step"] = "project", ["name"] = "  ", ["region"] = "GRU" });
        await Assert.That(response.Headers.Location!.ToString())
            .StartsWith("/organization?newOrganization=project&error=");

        var page = await app.GetPageAsync(client, response.Headers.Location!.ToString());
        await Assert.That(page.Contains("data-autoopen")).IsTrue();
        await Assert.That(page.Contains("Choose a project name.")).IsTrue();
    }

    [Test]
    public async Task OverlongProjectNameReopensTheProjectScreenWithTheError()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"neworg-long-{Guid.NewGuid():N}@example.test");
        await ReachProjectStepAsync(client);

        using var response = await DashboardTestApp.PostFormAsync(client, StepAction,
            new Dictionary<string, string> { ["step"] = "project", ["name"] = new string('a', 129), ["region"] = "GRU" });
        await Assert.That(response.Headers.Location!.ToString())
            .StartsWith("/organization?newOrganization=project&error=");

        var page = await app.GetPageAsync(client, response.Headers.Location!.ToString());
        await Assert.That(page.Contains("Choose a project name.")).IsTrue();
    }

    [Test]
    public async Task UnknownProjectRegionFallsBackToTheCatalogDefault()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"neworg-region-{Guid.NewGuid():N}@example.test");
        await ReachProjectStepAsync(client);

        // An unknown region is not an error: the endpoint keeps the wizard's own
        // catalog vocabulary and falls back to its first region.
        using var response = await DashboardTestApp.PostFormAsync(client, StepAction,
            new Dictionary<string, string> { ["step"] = "project", ["name"] = "nova-prod", ["region"] = "not-a-region" });
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/organization");

        var page = await app.GetPageAsync(client, "/organization");
        await Assert.That(page.Contains("nova-prod")).IsTrue();
    }

    [Test]
    public async Task SelectedPlanBillsTheMatchingStorePlan()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"neworg-plan-{Guid.NewGuid():N}@example.test");

        // The payment screen's Business card maps to the store's Dedicated plan.
        await ReachProjectStepAsync(client, plan: "business");
        using var project = await DashboardTestApp.PostFormAsync(client, StepAction,
            new Dictionary<string, string> { ["step"] = "project", ["name"] = "nova-prod", ["region"] = "GRU" });

        var usage = await app.GetPageAsync(client, "/usage?tab=billing");
        await Assert.That(usage.StatusCode).IsEqualTo(200);
        var organization = await app.GetPageAsync(client, "/organization");
        await Assert.That(organization.Contains("Nova Labs")).IsTrue();
        await Assert.That(usage.Contains("<span class=\"ts-kpi-value\">Dedicated</span>")).IsTrue();
    }

    [Test]
    public async Task ProjectStepRequiresActiveBusinessBilling()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        using var organization = await DashboardTestApp.PostFormAsync(client, StepAction,
            new Dictionary<string, string> { ["step"] = "organization", ["name"] = "Nova Labs" });
        using var response = await DashboardTestApp.PostFormAsync(client, StepAction,
            new Dictionary<string, string> { ["step"] = "project", ["name"] = "nova-prod", ["region"] = "GRU" });
        await Assert.That(response.Headers.Location!.ToString())
            .StartsWith("/organization?newOrganization=payment&error=");
    }

    [Test]
    public async Task FullJourneyCreatesTheTenantAndItsFirstProject()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"neworg-{Guid.NewGuid():N}@example.test");

        using var organization = await DashboardTestApp.PostFormAsync(client, StepAction,
            new Dictionary<string, string> { ["step"] = "organization", ["name"] = "Nova Labs" });
        await Assert.That(organization.Headers.Location!.ToString()).IsEqualTo("/organization?newOrganization=payment");

        // A Business tenant needs its own paid billing before it can hold resources.
        using var payment = await DashboardTestApp.PostFormAsync(client, StepAction,
            new Dictionary<string, string> { ["step"] = "payment-method" });
        var checkout = payment.Headers.Location!.ToString();
        using var completed = await DashboardTestApp.PostFormAsync(client, checkout + "/complete",
            new Dictionary<string, string>());
        await Assert.That(completed.Headers.Location!.ToString())
            .IsEqualTo("/organization?newOrganization=project&payment=success");

        using var project = await DashboardTestApp.PostFormAsync(client, StepAction,
            new Dictionary<string, string>
            {
                ["step"] = "project",
                ["name"] = "nova-prod",
                ["region"] = "GRU",
            });
        await Assert.That(project.Headers.Location!.ToString()).IsEqualTo("/organization");

        var page = await app.GetPageAsync(client, "/organization");
        // The switcher lists every org and the projects table is world-scoped,
        // so pin the *active* tenant: only the selected option and the Business
        // subtitle prove Nova Labs owns the new project.
        await Assert.That(page.Contains("selected=\"selected\">Nova Labs (Business)")).IsTrue();
        // The subtitle is only rendered for a non-Personal active org; assert
        // its halves because Razor HTML-encodes the middle dot.
        await Assert.That(page.Contains("Business workspace")).IsTrue();
        await Assert.That(page.Contains("paid billing required")).IsTrue();
        await Assert.That(page.Contains("nova-prod")).IsTrue();
    }
}
