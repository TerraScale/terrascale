using TerraScale.Dashboard.Testing;

namespace TerraScale.Dashboard.FastTests;

/// <summary>Every region map renders through the shared component
/// (Shared/Components/RegionMap.cs): one frame and one marker family with the
/// onboarding map's hover labels — flag, name, and latency — in every
/// selection mode. These lock in the unification so a page cannot quietly grow
/// its own map markup again.</summary>
public sealed class RegionMapTests
{
    [Test]
    public async Task OnboardingProjectStepRendersTheSharedSingleSelectMap()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();
        await app.RegisterAsync(client, $"map-step-{Guid.NewGuid():N}@example.test", "Member-tests-123!", "Map Step");
        await DashboardTestApp.PostFormAsync(client, "/onboarding/step",
            new Dictionary<string, string> { ["step"] = "payment-method", ["bypass"] = "1" });

        var page = await app.GetPageAsync(client, "/onboarding?step=project");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Contains("class=\"ts-region-frame\"")).IsTrue();
        await Assert.That(Count(page.Html, "class=\"ts-region-marker-input\" type=\"radio\"")).IsEqualTo(3);
        foreach (var hub in new[] { "GRU", "SFO", "FRA" })
        {
            await Assert.That(page.Contains($"name=\"region\" value=\"{hub}\"")).IsTrue();
        }

        foreach (var flag in new[] { "br", "us", "de" })
        {
            await Assert.That(page.Contains($"class=\"ts-region-marker-flag\" src=\"/brand/flags/{flag}.svg\"")).IsTrue();
        }

        await Assert.That(page.Contains("ts-onboarding-region-map")).IsFalse();
    }

    [Test]
    public async Task OrganizationProjectFormRendersTheSharedMultiSelectMap()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"map-org-{Guid.NewGuid():N}@example.test");

        var page = await app.GetPageAsync(client, "/organization");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(Count(page.Html, "class=\"ts-region-marker-input\" type=\"checkbox\"")).IsGreaterThanOrEqualTo(3);
        await Assert.That(Count(page.Html, "name=\"regions\" value=\"")).IsGreaterThanOrEqualTo(3);
        await Assert.That(page.Contains("class=\"ts-region-frame\"")).IsTrue();
        await Assert.That(page.Contains("ts-region-picker")).IsFalse();
        await Assert.That(page.Contains("ts-region-pin-btn")).IsFalse();
    }

    [Test]
    public async Task DatabaseDialogRendersTheSharedReadOnlyMap()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"map-db-{Guid.NewGuid():N}@example.test");

        var page = await app.GetPageAsync(client, "/databases");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Contains("class=\"ts-region-frame\"")).IsTrue();
        await Assert.That(page.Contains("ts-region-marker-readonly")).IsTrue();
        await Assert.That(page.Contains("ts-region-marker-selected")).IsTrue();
        await Assert.That(Count(page.Html, "class=\"ts-region-marker-input\"")).IsEqualTo(0);
        await Assert.That(page.Contains("ts-region-map-panel")).IsFalse();
    }

    private static int Count(string html, string value)
    {
        var count = 0;
        for (var index = html.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = html.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
