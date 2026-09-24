using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Dashboard.Features.Data.Models;
using TerraScale.Dashboard.Testing;

namespace TerraScale.Dashboard.SlowTests;

/// <summary>Server-rendered paging for Data schema and index collections.
/// The fixture is expanded beyond the ten-row page size so each tab exercises
/// its own list and preserves the active collection and tab in pager links.</summary>
public sealed class DataPaginationTests
{
    [Test]
    public async Task SchemaAndIndexListsPaginateIndependentlyAndPreserveContext()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        var world = DemoWorld(app);

        world.Schema = world.Schema with
        {
            Collections = Enumerable.Range(1, 11)
                .Select(index => new SchemaCollection(
                    $"collection-{index:00}",
                    [new SchemaField("id", "string", Required: true, Indexed: true)],
                    [new SchemaIndex($"collection_{index:00}_id_idx", ["id"])])).ToArray(),
        };
        world.Indexes = [.. Enumerable.Range(1, 11).Select(index => new IndexState(
            $"idx_pagination_{index:00}",
            $"pagination-index-{index:00}",
            ["id"],
            "active",
            10 + index,
            0,
            null,
            "2026-09-17T22:41:00Z"))];
        world.QueryTemplates = [.. Enumerable.Range(1, 11).Select(index => new QueryTemplate(
            $"tpl_pagination_{index:00}",
            $"SELECT * FROM collection_{index:00}",
            index,
            index,
            index,
            null))];
        world.IndexRecommendations = [.. Enumerable.Range(1, 11).Select(index => new IndexRecommendation(
            $"rec_pagination_{index:00}",
            [$"field_{index:00}"],
            $"Recommendation {index:00}",
            index,
            "recommended"))];

        var schema = await app.GetPageAsync(client, "/schema?tab=viewer&collection=orders&page=2");
        await Assert.That(schema.Contains("Showing 11–11 of 11 collections")).IsTrue();
        await Assert.That(schema.Contains("collection-11")).IsTrue();
        await Assert.That(schema.Contains("collection-01")).IsFalse();
        await Assert.That(schema.Html).Contains("tab=viewer&amp;collection=orders&amp;page=2");

        var indexes = await app.GetPageAsync(client, "/indexes?tab=overview&collection=orders&page=2");
        await Assert.That(indexes.Contains("Showing 11–11 of 11 indexes")).IsTrue();
        await Assert.That(indexes.Contains("pagination-index-11")).IsTrue();
        await Assert.That(indexes.Contains("pagination-index-01")).IsFalse();
        await Assert.That(indexes.Html).Contains("tab=overview&amp;collection=orders&amp;page=2");

        var templates = await app.GetPageAsync(client, "/indexes?tab=templates&collection=orders&page=2");
        await Assert.That(templates.Contains("Showing 11–11 of 11 query templates")).IsTrue();
        await Assert.That(templates.Contains("collection_11")).IsTrue();
        await Assert.That(templates.Contains("collection_01")).IsFalse();
        await Assert.That(templates.Html).Contains("tab=templates&amp;collection=orders&amp;page=2");

        var recommendations = await app.GetPageAsync(client, "/indexes?tab=recommendations&collection=orders&page=2");
        await Assert.That(recommendations.Contains("Showing 11–11 of 11 recommendations")).IsTrue();
        await Assert.That(recommendations.Contains("field_11")).IsTrue();
        await Assert.That(recommendations.Contains("field_01")).IsFalse();
        await Assert.That(recommendations.Html).Contains("tab=recommendations&amp;collection=orders&amp;page=2");
    }

    private static AccountWorld DemoWorld(DashboardTestApp app)
    {
        var store = app.Services.GetRequiredService<DashboardStore>();
        var property = typeof(DashboardStore).GetProperty("DemoWorld", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("DashboardStore.DemoWorld was not found.");
        return property.GetValue(store) as AccountWorld
            ?? throw new InvalidOperationException("DashboardStore.DemoWorld was not an AccountWorld.");
    }
}
