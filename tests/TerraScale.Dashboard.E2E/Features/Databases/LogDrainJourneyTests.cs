using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Features.Logs;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.E2E.Features.Databases;

[ClassDataSource<DashboardE2EFixture>]
public sealed class LogDrainJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task OwnerEditsOneProjectLogDrainKeepingItsHeaderAndCursors()
    {
        using var client = fixture.CreateClient();
        await DatabaseJourney.NativeWorkspaceAsync(client);
        var databaseId = await HttpJourney.CreateDatabaseAsync(client, "drained");
        using var me = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
        var projectId = me.RootElement.GetProperty("projectId").GetString()!;
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        using (var saved = await SaveAsync(client, csrf, databaseId, "https://otlp.example.com/v1/logs", "X-Key", "first-secret"))
        {
            await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        }

        var storage = fixture.Service<IDbContextFactory<DashboardDbContext>>();
        LogDrainRow first;
        await using (var db = await storage.CreateDbContextAsync())
        {
            first = await db.LogDrains.AsNoTracking().SingleAsync(row => row.ProjectId == projectId);
            db.LogDrainCursors.Add(new LogDrainCursorRow { Id = AccountService.NewId(), DrainId = first.Id, DatabaseId = databaseId, Cursor = AccountService.NewId() });
            await db.SaveChangesAsync();
        }

        using (var edited = await SaveAsync(client, csrf, databaseId, "https://otlp.example.com/v2/logs", "X-Key", ""))
        {
            await Assert.That(edited.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        }

        using (var renamed = await SaveAsync(client, csrf, databaseId, "https://otlp.example.com/v2/logs", "X-Other", ""))
        {
            await Assert.That(renamed.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            await Assert.That(await renamed.Content.ReadAsStringAsync()).Contains("Enter the header value.");
        }

        await using (var db = await storage.CreateDbContextAsync())
        {
            var drain = await db.LogDrains.AsNoTracking().Include(row => row.Cursors).SingleAsync(row => row.ProjectId == projectId);
            await Assert.That(drain.Id).IsEqualTo(first.Id);
            await Assert.That(drain.Revision).IsNotEqualTo(first.Revision);
            await Assert.That(drain.Url).IsEqualTo("https://otlp.example.com/v2/logs");
            await Assert.That(drain.HeaderCiphertext).IsEqualTo(first.HeaderCiphertext).Because("A blank value keeps the stored header while its name stays the same.");
            await Assert.That(drain.Cursors.Count).IsEqualTo(1).Because("Cursors survive edits, so nothing is sent twice.");
        }

        using (var removed = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/log-drain/delete", new() { ["_csrf"] = csrf, ["databaseId"] = databaseId }))
        {
            await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        }

        await using (var db = await storage.CreateDbContextAsync())
        {
            await Assert.That(await db.LogDrains.CountAsync(row => row.ProjectId == projectId)).IsEqualTo(0);
            await Assert.That(await db.LogDrainCursors.CountAsync(row => row.DrainId == first.Id)).IsEqualTo(0);
        }
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, string csrf, string databaseId, string url, string headerName, string headerValue)
    {
        return HttpJourney.PostFormAsync(client, "/api/v1/dashboard/log-drain", new()
        {
            ["_csrf"] = csrf,
            ["databaseId"] = databaseId,
            ["url"] = url,
            ["headerName"] = headerName,
            ["headerValue"] = headerValue,
        });
    }
}
