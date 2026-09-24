using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Dashboard.Features.Audit.Models;
using TerraScale.Dashboard.Testing;

namespace TerraScale.Dashboard.SlowTests;

/// <summary>Query-string behaviors: databases search, the audit filter set
/// (range, severity, search), data view tabs, and the api-keys filters.</summary>
public sealed class QueryFilterTests
{
    [Test]
    public async Task DatabasesSearchNarrowsByNameAndShowsEmptyState()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        var one = await app.GetPageAsync(client, "/databases?q=core-primary");
        await Assert.That(one.StatusCode).IsEqualTo(200);
        await Assert.That(one.Contains("core-primary")).IsTrue();
        await Assert.That(one.Contains("core-sessions")).IsFalse();

        var none = await app.GetPageAsync(client, "/databases?q=nothing-matches-this");
        await Assert.That(none.Contains("No databases match")).IsTrue();
        await Assert.That(none.Contains("Clear filter")).IsTrue();
        await Assert.That(none.Contains("core-primary")).IsFalse();
    }

    [Test]
    public async Task AuditRangeFilterBoundsTheEventWindow()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        var range90 = await app.GetPageAsync(client, "/audit?range=90d");
        await Assert.That(range90.Contains("Showing 1–5 of 5 events")).IsTrue();
        await Assert.That(range90.Contains("database.deleted")).IsTrue();

        var allActions = await app.GetPageAsync(client, "/audit?range=90d&action=all");
        await Assert.That(allActions.Contains("Showing 1–5 of 5 events")).IsTrue();

        var range24 = await app.GetPageAsync(client, "/audit?range=24h");
        await Assert.That(range24.Contains("5 retained under the current policy")).IsTrue();
        await Assert.That(range24.Contains("No audit events")).IsTrue();
    }

    [Test]
    public async Task AuditSearchAndSeverityNarrowRows()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        var actor = await app.GetPageAsync(client, "/audit?range=90d&search=ana");
        await Assert.That(actor.Contains("Showing 1–1 of 1 events")).IsTrue();
        await Assert.That(actor.Contains("member.role_changed")).IsTrue();

        var critical = await app.GetPageAsync(client, "/audit?range=90d&severity=critical");
        await Assert.That(critical.Contains("Showing 1–1 of 1 events")).IsTrue();
        await Assert.That(critical.Contains("database.deleted")).IsTrue();
    }

    [Test]
    public async Task DataViewsRenderDistinctPanelsPerTab()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        var queries = await app.GetPageAsync(client, "/data?view=queries");
        await Assert.That(queries.Contains("Saved queries")).IsTrue();

        var table = await app.GetPageAsync(client, "/data?view=table");
        await Assert.That(table.Contains("Collection documents")).IsTrue();
        await Assert.That(table.Contains("No documents loaded")).IsTrue();

        var documents = await app.GetPageAsync(client, "/data?view=table&database=core-primary&collection=orders");
        await Assert.That(documents.Contains("northwind")).IsTrue();
        await Assert.That(documents.Contains("acme")).IsTrue();
        await Assert.That(documents.Html).Contains("<td class=\"ts-table-mono\">active</td>");
        await Assert.That(documents.Html).Contains("<td class=\"ts-table-mono\">northwind</td>");
        await Assert.That(documents.Html).Contains("<td class=\"ts-table-num\">640</td>");
        await Assert.That(documents.Html).Contains("href=\"/data?view=editor&amp;database=core-primary&amp;collection=orders\"");

        var editor = await app.GetPageAsync(client, "/data?view=editor&database=core-primary&collection=orders");
        await Assert.That(editor.Contains("Document editor")).IsTrue();
        await Assert.That(editor.Html).Contains("href=\"/data?view=table&amp;database=core-primary&amp;collection=orders\"");

        var sql = await app.GetPageAsync(client, "/data?view=sql");
        await Assert.That(sql.Contains("SQL console")).IsTrue();
        await Assert.That(sql.Contains("Read-only statement accepted on orders.")).IsTrue();
    }

    [Test]
    public async Task ApiKeysSearchKindAndStatusFilters()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        var search = await app.GetPageAsync(client, "/api-keys?q=ci-deploy");
        await Assert.That(search.Contains("ci-deploy")).IsTrue();
        await Assert.That(search.Contains("local-dev")).IsFalse();

        var m2m = await app.GetPageAsync(client, "/api-keys?kind=m2m");
        await Assert.That(m2m.Contains("ingest-worker")).IsTrue();
        await Assert.That(m2m.Contains("ci-deploy")).IsFalse();
        await Assert.That(m2m.Contains("name=\"kind\" value=\"m2m\"")).IsTrue();
        await Assert.That(m2m.Contains("id=\"credential-kind-value\">M2M clients</span>")).IsTrue();

        var revoked = await app.GetPageAsync(client, "/api-keys?status=Revoked");
        await Assert.That(revoked.Contains("retired-key")).IsTrue();
        await Assert.That(revoked.Contains("ci-deploy")).IsFalse();
        await Assert.That(revoked.Contains("id=\"credential-status-value\">Revoked</span>")).IsTrue();
    }

    [Test]
    public async Task ApiKeysPaginationClampsInvalidPageAndKeepsFiltersInLinks()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        for (var index = 1; index <= 11; index++)
        {
            using var response = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/credentials/key",
                new Dictionary<string, string>
                {
                    ["name"] = $"pagination-key-{index:00}",
                    ["scopes"] = "documents:read",
                });
            await Assert.That(response.StatusCode).IsEqualTo(System.Net.HttpStatusCode.Redirect);
        }

        var pageTwo = await app.GetPageAsync(client, "/api-keys?q=pagination-key&page=2");
        await Assert.That(pageTwo.Contains("Showing 11–11 of 11")).IsTrue();
        await Assert.That(pageTwo.Contains("pagination-key-11")).IsTrue();
        await Assert.That(pageTwo.Contains("pagination-key-01")).IsFalse();

        var pager = Regex.Match(pageTwo.Html, "<nav class=\"ts-pager\"[^>]*>(.*?)</nav>", RegexOptions.Singleline).Value;
        await Assert.That(pager.Contains("q=pagination-key")).IsTrue();
        await Assert.That(pager.Contains("page=2")).IsTrue();

        var tooHigh = await app.GetPageAsync(client, "/api-keys?q=pagination-key&page=99");
        await Assert.That(tooHigh.Contains("Showing 11–11 of 11")).IsTrue();
        await Assert.That(tooHigh.Contains("pagination-key-11")).IsTrue();

        var tooLow = await app.GetPageAsync(client, "/api-keys?q=pagination-key&page=0");
        await Assert.That(tooLow.Contains("Showing 1–10 of 11")).IsTrue();
        await Assert.That(tooLow.Contains("pagination-key-01")).IsTrue();
        await Assert.That(tooLow.Contains("pagination-key-11")).IsFalse();
    }

    [Test]
    public async Task AuditPaginationClampsInvalidPageAndPreservesActiveFilters()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        AddDemoAuditEvents(app, 11);

        var pageTwo = await app.GetPageAsync(client, "/audit?search=pagination-audit&range=90d&page=2");
        await Assert.That(pageTwo.Contains("Showing 11–11 of 11 events")).IsTrue();
        await Assert.That(pageTwo.Contains("pagination-audit-11")).IsTrue();
        await Assert.That(pageTwo.Contains("pagination-audit-01")).IsFalse();

        var pager = Regex.Match(pageTwo.Html, "<nav class=\"ts-pager\"[^>]*>(.*?)</nav>", RegexOptions.Singleline).Value;
        await Assert.That(pager.Contains("search=pagination-audit")).IsTrue();
        await Assert.That(pager.Contains("range=90d")).IsTrue();
        await Assert.That(pager.Contains("page=2")).IsTrue();

        var tooHigh = await app.GetPageAsync(client, "/audit?search=pagination-audit&range=90d&page=99");
        await Assert.That(tooHigh.Contains("Showing 11–11 of 11 events")).IsTrue();
        await Assert.That(tooHigh.Contains("pagination-audit-11")).IsTrue();

        var tooLow = await app.GetPageAsync(client, "/audit?search=pagination-audit&range=90d&page=0");
        await Assert.That(tooLow.Contains("Showing 1–10 of 11 events")).IsTrue();
        await Assert.That(tooLow.Contains("pagination-audit-01")).IsTrue();
        await Assert.That(tooLow.Contains("pagination-audit-11")).IsFalse();
    }

    [Test]
    public async Task AccessMemberSearchAndRoleFilters()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        var search = await app.GetPageAsync(client, "/access?q=ana");
        await Assert.That(search.Contains("ana@terrascale.tech")).IsTrue();

        var noMatch = await app.GetPageAsync(client, "/access?q=zzz-no-such-member");
        await Assert.That(noMatch.Contains("No members match")).IsTrue();
        await Assert.That(noMatch.Contains("Clear filters")).IsTrue();

        var owners = await app.GetPageAsync(client, "/access?role=owner");
        await Assert.That(owners.Contains("mario@terrascale.tech")).IsTrue();
        await Assert.That(owners.Contains("No members match")).IsFalse();
    }

    private static void AddDemoAuditEvents(DashboardTestApp app, int count)
    {
        // The dashboard has no audit mutation endpoint. Use the demo account's
        // settable fixture state so this remains an end-to-end page assertion.
        var store = app.Services.GetRequiredService<DashboardStore>();
        var demoWorldProperty = typeof(DashboardStore).GetProperty(
            "DemoWorld", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("DashboardStore.DemoWorld was not found.");
        var demoWorld = demoWorldProperty.GetValue(store) as AccountWorld
            ?? throw new InvalidOperationException("DashboardStore.DemoWorld was not an AccountWorld.");

        demoWorld.AuditEvents =
        [
            .. demoWorld.AuditEvents,
            .. Enumerable.Range(1, count).Select(index => new AuditEvent(
                $"aud_pagination_{index:00}",
                "pagination@terrascale.tech",
                "pagination.event",
                $"pagination-audit-{index:00}",
                "test",
                "low",
                "dashboard",
                DateTimeOffset.UtcNow.AddMinutes(-index).ToString("O"))),
        ];
    }
}
