using TerraScale.Dashboard2.Testing;

namespace TerraScale.Dashboard2.SlowTests;

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
        await Assert.That(range90.Contains("Showing 5 of 5 events")).IsTrue();
        await Assert.That(range90.Contains("database.deleted")).IsTrue();

        var range24 = await app.GetPageAsync(client, "/audit?range=24h");
        await Assert.That(range24.Contains("Showing 0 of 5 events")).IsTrue();
        await Assert.That(range24.Contains("No audit events")).IsTrue();
    }

    [Test]
    public async Task AuditSearchAndSeverityNarrowRows()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        var actor = await app.GetPageAsync(client, "/audit?range=90d&search=ana");
        await Assert.That(actor.Contains("Showing 1 of 5 events")).IsTrue();
        await Assert.That(actor.Contains("member.role_changed")).IsTrue();

        var critical = await app.GetPageAsync(client, "/audit?range=90d&severity=critical");
        await Assert.That(critical.Contains("Showing 1 of 5 events")).IsTrue();
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

        var editor = await app.GetPageAsync(client, "/data?view=editor");
        await Assert.That(editor.Contains("Document editor")).IsTrue();

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

        var revoked = await app.GetPageAsync(client, "/api-keys?status=Revoked");
        await Assert.That(revoked.Contains("retired-key")).IsTrue();
        await Assert.That(revoked.Contains("ci-deploy")).IsFalse();
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
}
