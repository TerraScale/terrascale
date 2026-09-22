using System.Net;
using TerraScale.Dashboard.Testing;

namespace TerraScale.Dashboard.SlowTests;

public sealed class OnboardingWizardTests
{
    [Test]
    public async Task RegistrationCreatesPersonalWorkspaceAndHidesOrganizationStep()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();
        await app.RegisterAsync(client, $"personal-{Guid.NewGuid():N}@example.test", "Member-tests-123!", "Personal Probe");
        var page = await app.GetPageAsync(client, "/onboarding");
        await Assert.That(page.Contains("Personal workspace")).IsTrue();
        await Assert.That(page.Contains("Create your first organization")).IsFalse();
        await Assert.That(page.Contains("ACTIVATION STEP 2 OF 3")).IsTrue();
    }

    [Test]
    public async Task PaymentMethodUsesAnAccountBoundCheckoutSession()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();
        await app.RegisterAsync(client, $"checkout-{Guid.NewGuid():N}@example.test", "Member-tests-123!", "Checkout Probe");
        using var response = await DashboardTestApp.PostFormAsync(client, "/onboarding/step", new Dictionary<string, string> { ["step"] = "payment-method" });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.ToString();
        await Assert.That(location).StartsWith("/checkout/cs_local_");
        using var completed = await DashboardTestApp.PostFormAsync(client, location + "/complete", new Dictionary<string, string>());
        await Assert.That(completed.Headers.Location!.ToString()).IsEqualTo("/onboarding?payment=success");
        var page = await app.GetPageAsync(client, "/onboarding?payment=success");
        await Assert.That(page.Contains("Create your first project")).IsTrue();
    }

    [Test]
    public async Task PaymentBypassAdvancesDirectlyToProject()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();
        await app.RegisterAsync(client, $"bypass-{Guid.NewGuid():N}@example.test", "Member-tests-123!", "Bypass Probe");
        using var response = await DashboardTestApp.PostFormAsync(client, "/onboarding/step", new Dictionary<string, string> { ["step"] = "payment-method", ["bypass"] = "1" });
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/onboarding");
        var page = await app.GetPageAsync(client, "/onboarding");
        await Assert.That(page.Contains("Create your first project")).IsTrue();
    }

    [Test]
    public async Task BusinessWorkspaceDoesNotUsePersonalAllowance()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"business-{Guid.NewGuid():N}@example.test");
        using var created = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/organization/new-organization", new Dictionary<string, string>
        {
            ["step"] = "organization", ["name"] = "Acme Labs",
        });
        await Assert.That(created.Headers.Location!.ToString()).IsEqualTo("/organization?newOrganization=payment");
        var page = await app.GetPageAsync(client, "/organization");
        await Assert.That(page.Contains("Personal")).IsTrue();
        await Assert.That(page.Contains("Acme Labs")).IsTrue();
        await Assert.That(page.Contains("Business")).IsTrue();
    }

    [Test]
    public async Task PersonalWorkspaceCannotBeDeletedOrReplaced()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"archive-{Guid.NewGuid():N}@example.test");
        using var response = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/organization/deletion/request", new Dictionary<string, string> { ["confirmation"] = "Personal" });
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/organization?error=Personal%20workspaces%20cannot%20be%20deleted.");
        var page = await app.GetPageAsync(client, "/organization");
        await Assert.That(page.Contains("Personal workspace")).IsTrue();
    }

    [Test]
    public async Task ProjectStepOffersOnlyTheThreeTerraScaleRegionHubs()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();
        await app.RegisterAsync(client, $"regions-{Guid.NewGuid():N}@example.test", "Member-tests-123!", "Region Probe");
        await DashboardTestApp.PostFormAsync(client, "/onboarding/step",
            new Dictionary<string, string> { ["step"] = "payment-method", ["bypass"] = "1" });

        var page = await app.GetPageAsync(client, "/onboarding");
        foreach (var hub in new[] { "GRU", "SFO", "FRA" })
            await Assert.That(page.Contains($"name=\"region\" value=\"{hub}\"")).IsTrue();

        foreach (var alias in new[] { "us-east-1", "us-west-2", "eu-central-1", "eu-west-1", "sa-east-1" })
            await Assert.That(page.Contains($"value=\"{alias}\"")).IsFalse();

        await Assert.That(CountOccurrences(page.Html, "name=\"region\" value=\"")).IsEqualTo(3);
        foreach (var flag in new[] { "br", "us", "de" })
            await Assert.That(page.Contains($"/brand/flags/{flag}.svg")).IsTrue();
        foreach (var ping in new[] { "118 ms", "28 ms", "74 ms" })
            await Assert.That(page.Contains(ping)).IsTrue();
    }

    [Test]
    public async Task RegistrationRejectsInvalidFields()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();
        using var response = await DashboardTestApp.PostFormAsync(client, "/onboarding/register", new Dictionary<string, string>
        {
            ["displayName"] = "", ["email"] = "valid@example.test", ["password"] = "short", ["confirmPassword"] = "short", ["terms"] = "true",
        });
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/onboarding?error=This%20field%20is%20required.");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var index = haystack.IndexOf(needle, StringComparison.Ordinal); index >= 0;
             index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
