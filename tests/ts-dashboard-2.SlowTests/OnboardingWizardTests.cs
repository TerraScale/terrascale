using TerraScale.Dashboard2.Testing;

namespace TerraScale.Dashboard2.SlowTests;

/// <summary>The activation wizard: registration validation, every step
/// transition, the checkout detour, and the canceled-payment branch.</summary>
public sealed class OnboardingWizardTests
{
    private const string CheckoutSession = "cs_local_01JCHECKOUT7F3K9Q";

    [Test]
    public async Task RegistrationRejectsInvalidDisplayNameEmailAndPasswords()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();

        Task<System.Net.Http.HttpResponseMessage> Register(
            string displayName, string email, string password, string confirmPassword, string terms = "true") =>
            DashboardTestApp.PostFormAsync(client, "/onboarding/register", new Dictionary<string, string>
            {
                ["displayName"] = displayName,
                ["email"] = email,
                ["password"] = password,
                ["confirmPassword"] = confirmPassword,
                ["terms"] = terms,
            });

        using var missingName = await Register("", "valid@example.test", "Long-enough-password-1!", "Long-enough-password-1!");
        await Assert.That(missingName.Headers.Location!.ToString())
            .IsEqualTo("/onboarding?error=This%20field%20is%20required.");

        using var invalidEmail = await Register("Probe User", "not-an-email", "Long-enough-password-1!", "Long-enough-password-1!");
        await Assert.That(invalidEmail.Headers.Location!.ToString())
            .IsEqualTo("/onboarding?error=Enter%20a%20valid%20email%20address.");

        using var shortPassword = await Register("Probe User", $"short-{Guid.NewGuid():N}@example.test", "short", "short");
        await Assert.That(shortPassword.Headers.Location!.ToString())
            .IsEqualTo("/onboarding?error=Use%20at%20least%2012%20characters.");

        using var mismatched = await Register("Probe User", $"mismatch-{Guid.NewGuid():N}@example.test", "Long-enough-password-1!", "a-different-password");
        await Assert.That(mismatched.Headers.Location!.ToString())
            .IsEqualTo("/onboarding?error=Passwords%20do%20not%20match.");

        using var withoutTerms = await Register("Probe User", $"noterms-{Guid.NewGuid():N}@example.test", "Long-enough-password-1!", "Long-enough-password-1!", terms: "");
        await Assert.That(withoutTerms.Headers.Location!.ToString())
            .IsEqualTo("/onboarding?error=You%20must%20accept%20the%20terms%20to%20continue.");
    }

    [Test]
    public async Task RegistrationRejectsDuplicateEmail()
    {
        using var app = new DashboardTestApp();
        var email = $"dupe-{Guid.NewGuid():N}@example.test";
        using var client = app.CreateClient();
        using var first = await app.RegisterAsync(client, email, "Member-tests-123!", "Duplicate Probe");
        await Assert.That(first.Headers.Location!.ToString()).IsEqualTo("/onboarding");
        using var second = await app.RegisterAsync(client, email, "Member-tests-123!", "Duplicate Probe");
        await Assert.That(second.Headers.Location!.ToString())
            .IsEqualTo("/onboarding?error=An%20account%20with%20this%20email%20already%20exists.");
    }

    [Test]
    public async Task RegistrationLandsSignedInOnTheOrganizationStep()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();
        using var response = await app.RegisterAsync(client, $"org-{Guid.NewGuid():N}@example.test", "Member-tests-123!", "Org Probe");
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/onboarding");
        var page = await app.GetPageAsync(client, "/onboarding");
        await Assert.That(page.Contains("Create your first organization")).IsTrue();
        await Assert.That(page.Contains("ACTIVATION STEP 2 OF 5")).IsTrue();
    }

    [Test]
    public async Task OrganizationStepAdvancesToPaymentMethod()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();
        await app.RegisterAsync(client, $"pay-{Guid.NewGuid():N}@example.test", "Member-tests-123!", "Pay Probe");
        using var response = await DashboardTestApp.PostFormAsync(client, "/onboarding/step",
            new Dictionary<string, string> { ["step"] = "organization", ["name"] = "Probe Labs" });
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/onboarding?step=payment-method");
        var page = await app.GetPageAsync(client, "/onboarding?step=payment-method");
        await Assert.That(page.Contains("Register a payment method")).IsTrue();
        await Assert.That(page.Contains("Continue without payment")).IsTrue();
    }

    [Test]
    public async Task PaymentMethodWithoutBypassRedirectsToCheckout()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();
        await app.RegisterAsync(client, $"checkout-{Guid.NewGuid():N}@example.test", "Member-tests-123!", "Checkout Probe");
        using var response = await DashboardTestApp.PostFormAsync(client, "/onboarding/step",
            new Dictionary<string, string> { ["step"] = "payment-method" });
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo($"/checkout/{CheckoutSession}");
    }

    [Test]
    public async Task PaymentBypassAdvancesToValidationStep()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();
        await app.RegisterAsync(client, $"bypass-{Guid.NewGuid():N}@example.test", "Member-tests-123!", "Bypass Probe");
        using var response = await DashboardTestApp.PostFormAsync(client, "/onboarding/step",
            new Dictionary<string, string> { ["step"] = "payment-method", ["bypass"] = "1" });
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/onboarding?step=payment-validation");
        var page = await app.GetPageAsync(client, "/onboarding?step=payment-validation");
        await Assert.That(page.Contains("Validate your payment method")).IsTrue();
        await Assert.That(page.Contains("Validation pending")).IsTrue();
    }

    [Test]
    public async Task CheckoutCompleteValidatesPaymentAndReachesTheProjectStep()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();
        await app.RegisterAsync(client, $"complete-{Guid.NewGuid():N}@example.test", "Member-tests-123!", "Complete Probe");
        await DashboardTestApp.PostFormAsync(client, "/onboarding/step",
            new Dictionary<string, string> { ["step"] = "organization", ["name"] = "Probe Labs" });

        using var completed = await DashboardTestApp.PostFormAsync(client, $"/checkout/{CheckoutSession}/complete", new Dictionary<string, string>());
        await Assert.That(completed.Headers.Location!.ToString()).IsEqualTo("/onboarding?payment=success");
        var page = await app.GetPageAsync(client, "/onboarding?payment=success");
        await Assert.That(page.Contains("Create your first project")).IsTrue();
        var validation = await app.GetPageAsync(client, "/onboarding?payment=success&step=payment-validation");
        await Assert.That(validation.Contains("Payment method validated")).IsTrue();
    }

    [Test]
    public async Task CheckoutCancelShowsTheSavedProgressBanner()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();
        await app.RegisterAsync(client, $"cancel-{Guid.NewGuid():N}@example.test", "Member-tests-123!", "Cancel Probe");
        using var response = await DashboardTestApp.PostFormAsync(client, $"/checkout/{CheckoutSession}/cancel", new Dictionary<string, string>());
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/onboarding?payment=canceled");
        var page = await app.GetPageAsync(client, "/onboarding?payment=canceled");
        await Assert.That(page.Contains("Payment setup canceled")).IsTrue();
        await Assert.That(page.Contains("Your earlier account progress is still saved.")).IsTrue();
    }

    [Test]
    public async Task ProjectStepNamesThePersonalProjectAndOpensTheDashboard()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();
        await app.RegisterAsync(client, $"project-{Guid.NewGuid():N}@example.test", "Member-tests-123!", "Project Probe");
        await DashboardTestApp.PostFormAsync(client, "/onboarding/step",
            new Dictionary<string, string> { ["step"] = "organization", ["name"] = "Probe Labs" });
        await DashboardTestApp.PostFormAsync(client, "/onboarding/step",
            new Dictionary<string, string> { ["step"] = "payment-method", ["bypass"] = "1" });
        using var response = await DashboardTestApp.PostFormAsync(client, "/onboarding/step",
            new Dictionary<string, string> { ["step"] = "project", ["name"] = "probe-prod", ["region"] = "GRU" });
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/");

        var overview = await app.GetPageAsync(client, "/");
        await Assert.That(overview.Heading).IsEqualTo("Overview");
        var organization = await app.GetPageAsync(client, "/organization");
        await Assert.That(organization.Contains("probe-prod")).IsTrue();
        await Assert.That(organization.Contains("Personal")).IsFalse();
    }

    [Test]
    public async Task UnknownStepKeyRedirectsToTheWizard()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        using var response = await DashboardTestApp.PostFormAsync(client, "/onboarding/step",
            new Dictionary<string, string> { ["step"] = "not-a-step" });
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/onboarding");
    }
}
