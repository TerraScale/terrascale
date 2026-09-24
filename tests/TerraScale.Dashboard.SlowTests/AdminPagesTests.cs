using System.Text;
using TerraScale.Dashboard.Features.Admin.Presentation;
using TerraScale.Dashboard.Testing;

namespace TerraScale.Dashboard.SlowTests;

/// <summary>The owner-only admin surface behind the e2e dev-shim
/// (POST /api/v1/testing/dev-shim/reset { promoteOwner: true }): all six
/// pages render with their seeded rows, plus the member-side promotion path.</summary>
public sealed class AdminPagesTests
{
    static AdminPagesTests() =>
        // The shim gates on the process-level DASHBOARD_E2E flag; the test host
        // usually carries it from WithWebHostBuilder, but set it so the gate
        // holds even when it does not.
        Environment.SetEnvironmentVariable("DASHBOARD_E2E", "1");

    private static async Task<HttpClient> CreateOwnerClientAsync(DashboardTestApp app)
    {
        var client = app.CreateDemoClient();
        using var content = new StringContent("""{"promoteOwner":true}""", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/v1/testing/dev-shim/reset", content);
        await Assert.That((int)response.StatusCode).IsEqualTo(200);
        return client;
    }

    [Test]
    public async Task AdminRootRedirectsToUsers()
    {
        using var app = new DashboardTestApp();
        using var client = await CreateOwnerClientAsync(app);
        using var response = await client.GetAsync("/admin");
        await Assert.That((int)response.StatusCode).IsEqualTo(302);
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/admin/users");
    }

    [Test]
    public async Task AdminUsersPageRendersSeededRoster()
    {
        using var app = new DashboardTestApp();
        using var client = await CreateOwnerClientAsync(app);
        var page = await app.GetPageAsync(client, "/admin/users");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Users");
        await Assert.That(page.Contains("Owner-only user administration.")).IsTrue();
        await Assert.That(page.Contains("mario@terrascale.tech")).IsTrue();
        await Assert.That(page.Contains("dana@northwind.io")).IsTrue();
    }

    [Test]
    public async Task AdminUsersFilterNarrowsTheRoster()
    {
        using var app = new DashboardTestApp();
        using var client = await CreateOwnerClientAsync(app);
        var page = await app.GetPageAsync(client, "/admin/users?q=northwind");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Contains("igor@northwind.io")).IsTrue();
        await Assert.That(page.Contains("Ravi Patel")).IsFalse();
    }

    [Test]
    public async Task AdminDatabasesPageRendersSeededInventory()
    {
        using var app = new DashboardTestApp();
        using var client = await CreateOwnerClientAsync(app);
        var page = await app.GetPageAsync(client, "/admin/databases");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Databases");
        await Assert.That(page.Contains("Owner-only database inventory.")).IsTrue();
        await Assert.That(page.Contains("core-primary")).IsTrue();
        await Assert.That(page.Contains("edge-events")).IsTrue();
    }

    [Test]
    public async Task AdminEstatePageRendersRegionsHostsAndFacts()
    {
        using var app = new DashboardTestApp();
        using var client = await CreateOwnerClientAsync(app);
        var page = await app.GetPageAsync(client, "/admin/estate");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Estate");
        await Assert.That(page.Contains("Virginia, US")).IsTrue();
        await Assert.That(page.Contains("eu-west-1 degraded")).IsTrue();
        await Assert.That(page.Contains("Estate operations")).IsTrue();
    }

    [Test]
    public async Task AdminOrganizationsPageRendersSeededTenants()
    {
        using var app = new DashboardTestApp();
        using var client = await CreateOwnerClientAsync(app);
        var page = await app.GetPageAsync(client, "/admin/organizations");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Organizations");
        await Assert.That(page.Contains("Owner-only organization inventory.")).IsTrue();
        await Assert.That(page.Contains("TerraScale Labs")).IsTrue();
        await Assert.That(page.Contains("Helios Research")).IsTrue();
    }

    [Test]
    public async Task AdminUsagePageRendersPlatformSummary()
    {
        using var app = new DashboardTestApp();
        using var client = await CreateOwnerClientAsync(app);
        var page = await app.GetPageAsync(client, "/admin/usage");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Usage");
        await Assert.That(page.Contains("Owner-only platform usage summary.")).IsTrue();
        await Assert.That(page.Contains("organizations")).IsTrue();
        await Assert.That(page.Contains("databases")).IsTrue();
    }

    [Test]
    public async Task AdminPricingPageRendersPlanCatalog()
    {
        using var app = new DashboardTestApp();
        using var client = await CreateOwnerClientAsync(app);
        var page = await app.GetPageAsync(client, "/admin/pricing");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Pricing");
        await Assert.That(page.Contains("Owner-only pricing plan administration.")).IsTrue();
        await Assert.That(page.Contains("Starter")).IsTrue();
        await Assert.That(page.Contains("Edit pricing plans")).IsTrue();
    }

    [Test]
    public async Task AdminTabsKeepSecondaryViewsLinkable()
    {
        using var app = new DashboardTestApp();
        using var client = await CreateOwnerClientAsync(app);

        var estate = await app.GetPageAsync(client, "/admin/estate?tab=hosts&q=eu");
        await Assert.That(estate.StatusCode).IsEqualTo(200);
        await Assert.That(estate.Contains("href=\"/admin/estate?tab=operations\"")).IsTrue();
        await Assert.That(estate.Contains("aria-current=\"page\"")).IsTrue();

        var pricing = await app.GetPageAsync(client, "/admin/pricing?tab=create");
        await Assert.That(pricing.StatusCode).IsEqualTo(200);
        await Assert.That(pricing.Contains("Create pricing plan")).IsTrue();
    }

    [Test]
    public async Task AdminPaginationClampsPageAndPreservesFilters()
    {
        var rows = Enumerable.Range(1, 25).ToArray();
        var page = AdminPresentation.Paginate(rows, "2");
        await Assert.That(page.Number).IsEqualTo(2);
        await Assert.That(page.Rows).IsEquivalentTo(Enumerable.Range(11, 10));

        var pager = AdminPresentation.Pager("/admin/estate", page.Number, page.TotalPages, page.TotalRows, "eu west", "hosts").Value!;
        await Assert.That(pager.Contains("tab=hosts")).IsTrue();
        await Assert.That(pager.Contains("q=eu%20west")).IsTrue();
        await Assert.That(pager.Contains("page=1")).IsTrue();
        await Assert.That(pager.Contains("page=3")).IsTrue();
    }

    [Test]
    public async Task PromoteFreshMemberAddsAdminMenuAndListsRegisteredAccounts()
    {
        using var app = new DashboardTestApp();
        var email = $"admin-{Guid.NewGuid():N}@example.test";
        using var client = await app.CreateMemberClientAsync(email);

        var before = await app.GetPageAsync(client, "/organization");
        await Assert.That(before.Contains("href=\"/admin\" target=\"_blank\"")).IsFalse();

        using var content = new StringContent("""{"promoteOwner":true}""", Encoding.UTF8, "application/json");
        using var shim = await client.PostAsync("/api/v1/testing/dev-shim/reset", content);
        await Assert.That((int)shim.StatusCode).IsEqualTo(200);

        var after = await app.GetPageAsync(client, "/organization");
        await Assert.That(after.Contains("href=\"/admin\" target=\"_blank\"")).IsTrue();

        var users = await app.GetPageAsync(client, "/admin/users");
        await Assert.That(users.StatusCode).IsEqualTo(200);
        await Assert.That(users.Contains(email)).IsTrue();
        // Fresh admin sessions see the degraded states cleared by promotion.
        var overview = await app.GetPageAsync(client, "/");
        await Assert.That(overview.Contains("Monitoring snapshot may be stale")).IsFalse();
    }
}
