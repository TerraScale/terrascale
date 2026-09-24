using Microsoft.AspNetCore.Http;
using System.Text.RegularExpressions;
using TerraScale.Dashboard.Features.Engine.Pages.Engine;
using TerraScale.Dashboard.Features.Engine.ViewModels;
using TerraScale.Dashboard.Shared.Layout;
using TerraScale.Dashboard.Testing;

namespace TerraScale.Dashboard.SlowTests;

public sealed class DatabaseAndEnginePaginationTests
{
    [Test]
    public async Task EngineBadgesUseTotalsWhenTablesArePaginated()
    {
        var store = new DashboardStore();
        var model = new EnginePageModel(
            Configured: true,
            Url: "http://engine.test",
            Health: null,
            HealthError: "unavailable",
            Regions: [.. Enumerable.Range(1, 10).Select(index => new EngineRegionRow($"r{index}", $"Region {index}", "BR", index, index, true))],
            HostCount: 11,
            CatalogError: null,
            Metrics: [.. Enumerable.Range(1, 10).Select(index => new EngineMetricInfo($"metric_{index}", $"Metric {index}"))],
            SampleCount: 11,
            MetricsError: null)
        {
            Shell = ShellFactory.Create(store, "/engine"),
            RegionsPage = 2,
            RegionsPageCount = 2,
            RegionsTotal = 11,
            MetricsPage = 2,
            MetricsPageCount = 2,
            MetricsTotal = 11,
        };

        Microsoft.AspNetCore.Http.IResult result = Results.RazorSlice(EngineSlice.Create(model));
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var html = await reader.ReadToEndAsync();

        await Assert.That(html).Contains("11 regions · 11 hosts");
        await Assert.That(html).Contains("11 families");
    }

    [Test]
    public async Task EnginePaginationLinksPreserveTheOtherTablePage()
    {
        var model = EnginePageModel.NotConfigured(null!, "") with { RegionsPage = 2, MetricsPage = 3 };

        await Assert.That(model.RegionsHref(2)).IsEqualTo("/engine?regionsPage=2&metricsPage=3");
        await Assert.That(model.MetricsHref(2)).IsEqualTo("/engine?regionsPage=2&metricsPage=2");
    }

    [Test]
    public async Task DatabasesPaginationClampsPageAndPreservesSearchFilter()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        for (var index = 1; index <= 11; index++)
        {
            using var response = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/databases/create",
                new Dictionary<string, string> { ["name"] = $"pagination-db-{index:00}" });
            await Assert.That(response.StatusCode).IsEqualTo(System.Net.HttpStatusCode.Redirect);
        }

        var pageTwo = await app.GetPageAsync(client, "/databases?q=pagination-db&page=2");
        await Assert.That(pageTwo.Contains("Showing 11–11 of 11 databases")).IsTrue();
        await Assert.That(pageTwo.Contains("pagination-db-11")).IsTrue();
        await Assert.That(pageTwo.Contains("pagination-db-01")).IsFalse();

        var databaseId = Regex.Match(pageTwo.Html, "href=\"/databases/(?<id>[^\"]+)\" title=\"pagination-db-11\"").Groups["id"].Value;
        await Assert.That(databaseId.Length).IsGreaterThan(0);
        using var rejected = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/databases/delete",
            new Dictionary<string, string>
            {
                ["databaseId"] = databaseId,
                ["confirm"] = "wrong-name",
                ["q"] = "pagination-db",
                ["page"] = "2",
            });
        await Assert.That(rejected.Headers.Location?.ToString()).IsEqualTo($"/databases?delete={databaseId}&q=pagination-db&page=2");

        var rejectedPage = await app.GetPageAsync(client, $"/databases?delete={databaseId}&q=pagination-db&page=2");
        await Assert.That(rejectedPage.Contains("Enter the current database name exactly to confirm deletion.")).IsTrue();
        await Assert.That(rejectedPage.Contains("pagination-db-11")).IsTrue();

        var pager = Regex.Match(pageTwo.Html, "<nav class=\"ts-pager\"[^>]*>(.*?)</nav>", RegexOptions.Singleline).Value;
        await Assert.That(pager.Contains("q=pagination-db")).IsTrue();
        await Assert.That(pager.Contains("page=2")).IsTrue();

        var tooHigh = await app.GetPageAsync(client, "/databases?q=pagination-db&page=99");
        await Assert.That(tooHigh.Contains("Showing 11–11 of 11 databases")).IsTrue();
        await Assert.That(tooHigh.Contains("pagination-db-11")).IsTrue();

        var tooLow = await app.GetPageAsync(client, "/databases?q=pagination-db&page=0");
        await Assert.That(tooLow.Contains("Showing 1–10 of 11 databases")).IsTrue();
        await Assert.That(tooLow.Contains("pagination-db-01")).IsTrue();
        await Assert.That(tooLow.Contains("pagination-db-11")).IsFalse();
    }
}
