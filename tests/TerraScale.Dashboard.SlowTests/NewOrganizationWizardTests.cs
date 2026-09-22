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
        await Assert.That(page.Contains("ts-onboarding-card")).IsTrue();
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
        await Assert.That(page.Contains("data-autoopen")).IsTrue();
        await Assert.That(page.Contains("STEP 2 OF 3")).IsTrue();
        await Assert.That(page.Contains("Register a payment method")).IsTrue();
        await Assert.That(page.Contains("Starter")).IsTrue();
        // A Business organization always carries its own paid billing, so the
        // onboarding screen's "continue without payment" action is not offered.
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

        var page = await app.GetPageAsync(client, "/organization?newOrganization=project&payment=canceled");
        await Assert.That(page.Contains("data-autoopen")).IsTrue();
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
        await Assert.That(page.Contains("Nova Labs")).IsTrue();
        await Assert.That(page.Contains("nova-prod")).IsTrue();
    }
}
