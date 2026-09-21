namespace TerraScale.Dashboard.SlowTests;

/// <summary>
/// The Dashboard "create another organization" journey: it repeats only the
/// three onboarding screens a new tenant needs (organization, payment,
/// project), reusing the activation wizard's presentation.
/// </summary>
public sealed class NewOrganizationWizardTests
{
    private const string CheckoutSession = "cs_local_01JCHECKOUT7F3K9Q";
    private const string CheckoutWithReturn = $"/checkout/{CheckoutSession}?return=%2Forganizations%2Fnew";

    [Test]
    public async Task WizardReusesTheOnboardingStepScreensForThreeSteps()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        var page = await app.GetPageAsync(client, "/organizations/new");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Create organization");
        await Assert.That(page.Contains("STEP 1 OF 3")).IsTrue();
        await Assert.That(page.Contains("ts-onboarding-card")).IsTrue();
        await Assert.That(page.Contains("ts-onboarding-step")).IsTrue();
        await Assert.That(page.Contains("Create your first organization")).IsFalse();
        await Assert.That(page.Contains("Organization name")).IsTrue();
    }

    [Test]
    public async Task OrganizationStepAdvancesToThePaymentScreen()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        using var response = await DashboardTestApp.PostFormAsync(client, "/organizations/new/step",
            new Dictionary<string, string> { ["step"] = "organization", ["name"] = "Nova Labs" });
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/organizations/new?step=payment");

        var page = await app.GetPageAsync(client, "/organizations/new?step=payment");
        await Assert.That(page.Contains("STEP 2 OF 3")).IsTrue();
        await Assert.That(page.Contains("Register a payment method")).IsTrue();
        await Assert.That(page.Contains("Continue without payment")).IsTrue();
        await Assert.That(page.Contains("Starter")).IsTrue();
    }

    [Test]
    public async Task PaymentBypassAdvancesToTheProjectScreen()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        using var response = await DashboardTestApp.PostFormAsync(client, "/organizations/new/step",
            new Dictionary<string, string> { ["step"] = "payment-method", ["bypass"] = "1" });
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/organizations/new?step=project");

        var page = await app.GetPageAsync(client, "/organizations/new?step=project");
        await Assert.That(page.Contains("STEP 3 OF 3")).IsTrue();
        await Assert.That(page.Contains("Create your first project")).IsTrue();
        await Assert.That(page.Contains("Primary regions")).IsTrue();
    }

    [Test]
    public async Task SecurePaymentOpensCheckoutThatResumesTheWizard()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        using var response = await DashboardTestApp.PostFormAsync(client, "/organizations/new/step",
            new Dictionary<string, string> { ["step"] = "payment-method" });
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo(CheckoutWithReturn);

        var checkout = await app.GetPageAsync(client, CheckoutWithReturn);
        await Assert.That(checkout.Contains("name=\"returnTo\" value=\"/organizations/new\"")).IsTrue();

        using var completed = await DashboardTestApp.PostFormAsync(client, $"/checkout/{CheckoutSession}/complete",
            new Dictionary<string, string> { ["returnTo"] = "/organizations/new" });
        await Assert.That(completed.Headers.Location!.ToString()).IsEqualTo("/organizations/new?payment=success");

        var resumed = await app.GetPageAsync(client, "/organizations/new?payment=success");
        await Assert.That(resumed.Contains("Create your first project")).IsTrue();
    }

    [Test]
    public async Task CanceledCheckoutReturnsToPaymentWithTheSavedProgressBanner()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        using var response = await DashboardTestApp.PostFormAsync(client, $"/checkout/{CheckoutSession}/cancel",
            new Dictionary<string, string> { ["returnTo"] = "/organizations/new" });
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/organizations/new?payment=canceled");

        var page = await app.GetPageAsync(client, "/organizations/new?payment=canceled");
        await Assert.That(page.Contains("Payment setup canceled")).IsTrue();
        await Assert.That(page.Contains("Register a payment method")).IsTrue();
    }

    [Test]
    public async Task CheckoutIgnoresAnOffOriginReturnTarget()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();

        using var response = await DashboardTestApp.PostFormAsync(client, $"/checkout/{CheckoutSession}/complete",
            new Dictionary<string, string> { ["returnTo"] = "//evil.example/steal" });
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/onboarding?payment=success");
    }

    [Test]
    public async Task UnknownStepReturnsToTheFirstScreen()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        using var response = await DashboardTestApp.PostFormAsync(client, "/organizations/new/step",
            new Dictionary<string, string> { ["step"] = "not-a-step" });
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/organizations/new");
    }

    [Test]
    public async Task FullJourneyCreatesTheTenantAndItsFirstProject()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"neworg-{Guid.NewGuid():N}@example.test");

        using var organization = await DashboardTestApp.PostFormAsync(client, "/organizations/new/step",
            new Dictionary<string, string> { ["step"] = "organization", ["name"] = "Nova Labs" });
        await Assert.That(organization.Headers.Location!.ToString()).IsEqualTo("/organizations/new?step=payment");

        using var payment = await DashboardTestApp.PostFormAsync(client, "/organizations/new/step",
            new Dictionary<string, string> { ["step"] = "payment-method", ["bypass"] = "1" });
        await Assert.That(payment.Headers.Location!.ToString()).IsEqualTo("/organizations/new?step=project");

        using var project = await DashboardTestApp.PostFormAsync(client, "/organizations/new/step",
            new Dictionary<string, string>
            {
                ["step"] = "project",
                ["name"] = "nova-prod",
                ["region"] = "us-east-1",
            });
        await Assert.That(project.Headers.Location!.ToString()).IsEqualTo("/organization");

        var page = await app.GetPageAsync(client, "/organization");
        await Assert.That(page.Contains("Nova Labs")).IsTrue();
        await Assert.That(page.Contains("nova-prod")).IsTrue();
    }
}
