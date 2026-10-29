using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Features.Profile;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E
{
    public sealed partial class StateSdkScenario
    {
        private void MapSqlCleanupRoutes(WebApplication app)
        {
            app.MapPost("/__test/sql-cleanup/seed", async (IDashboardState state) =>
            {
                var org = AccountService.NewId(); var project = AccountService.NewId(); var database = AccountService.NewId();
                var history = Enumerable.Range(0, 7).Select(index => "SELECT '" + index + new string('x', 16370) + "'").ToArray();
                async Task<string> WorkspaceAsync(string owner, string group, string db)
                {
                    var user = AccountService.NewId();
                    var key = owner + "/" + group + "/" + db + "/" + user;
                    await state.PutAsync(SqlWorkspace.Collection, key, JsonSerializer.SerializeToElement(new SqlWorkspaceRecord(owner, group, db, user,
                        "SELECT '" + new string('d', 16000) + "'", history)), TerraBasePrecondition.Absent, default);
                    return key;
                }
                for (var index = 0; index < 67; index++)
                {
                    await WorkspaceAsync(org, project, database);
                }

                var retained = new[] { await WorkspaceAsync(org, project, AccountService.NewId()),
                    await WorkspaceAsync(org, AccountService.NewId(), database), await WorkspaceAsync(AccountService.NewId(), project, database) };
                await DatabaseWorkspaceCleanup.RememberAsync(state, new(org, project, database), default);
                for (var index = 0; index < 34; index++)
                {
                    await DatabaseWorkspaceCleanup.RememberAsync(state, new(org, project, AccountService.NewId()), default);
                }

                await DatabaseWorkspaceCleanup.RememberAsync(state, new(org, AccountService.NewId(), AccountService.NewId()), default);
                await DatabaseWorkspaceCleanup.RememberAsync(state, new(AccountService.NewId(), project, AccountService.NewId()), default);
                return Results.Json(new { org, project, database, retained });
            });
            app.MapPost("/__test/sql-cleanup/replay", async (IDashboardState state) =>
            {
                try { await DatabaseWorkspaceCleanup.ReplayAsync(state, default); return Results.NoContent(); }
                catch (HttpRequestException) { return Results.StatusCode(503); }
            });
            // Simulates an already admitted write reaching persistence after erasure's scan.
            app.MapPost("/__test/sql-cleanup/late/{org}/{project}/{database}", async (string org, string project, string database, IDashboardState state) =>
            {
                var user = AccountService.NewId();
                await state.PutAsync(SqlWorkspace.Collection, org + "/" + project + "/" + database + "/" + user,
                    JsonSerializer.SerializeToElement(new SqlWorkspaceRecord(org, project, database, user, "late", [])), TerraBasePrecondition.Absent, default);
                return Results.NoContent();
            });
            app.MapPost("/__test/sql-cleanup/project/{org}/{project}", async (string org, string project, IDashboardState state) =>
            {
                await new DeletionProjectRecords(state).CleanAsync(org, project, default);
                return Results.NoContent();
            });
            app.MapPost("/__test/sql-cleanup/organization/{org}", async (string org, IDashboardState state) =>
            {
                await new DeletionRelatedRecords(state, app.Services.GetRequiredService<DashboardSessions>()).CleanOrganizationAsync(org, default);
                return Results.NoContent();
            });
            app.MapGet("/__test/sql-cleanup/workspace", async (string key, IDashboardState state) =>
                Results.Json(await state.GetAsync(SqlWorkspace.Collection, key, default)));
        }

        private sealed partial class StateProducer
        {
            private readonly List<CleanupQuery> cleanupQueries = [];
            private int sqlWorkspaceLists;
            private sealed record CleanupQuery(string Collection, JsonElement? Filter, IReadOnlyList<string>? Fields, int Limit, string? Cursor, int Items, int Bytes);
            private async Task<IResult> QueryAsync(string name, HttpContext http)
            {
                using var json = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                var body = json.RootElement;
                Require(body.GetProperty("limit").GetInt32() == 32, "Cleanup must use fixed bounded query pages.");
                var query = new TerraBaseQuery
                {
                    Filter = body.TryGetProperty("filter", out var filter) && filter.ValueKind != JsonValueKind.Null ? filter.Clone() : null,
                    Project = body.TryGetProperty("project", out var fields) && fields.ValueKind != JsonValueKind.Null ? fields.EnumerateArray().Select(field => field.GetString()!).ToArray() : null,
                    Cursor = body.TryGetProperty("cursor", out var cursor) ? cursor.GetString() : null,
                    Limit = body.GetProperty("limit").GetInt32()
                };
                lock (gate)
                {
                    if (!collections.TryGetValue(name, out var collection))
                    {
                        return Error(404, "unknown_collection");
                    }

                    var page = TestStateQuery.Page(collection.Documents.Where(pair => !pair.Value.Deleted)
                        .Select(pair => new TerraBaseDocument(pair.Key, pair.Value.Version, "value", pair.Value.Value)), query);
                    if (name == SqlWorkspace.Collection)
                    {
                        Require(query.Filter is not null && query.Project is { Count: 4 } &&
                            query.Project.Order(StringComparer.Ordinal).SequenceEqual(new[] { "/DatabaseId", "/OrganizationId", "/ProjectId", "/UserId" }),
                            "Workspace cleanup must filter and project only owner IDs.");
                    }

                    if (name == DatabaseWorkspaceCleanup.Collection)
                    {
                        Require(page.Items.All(item => item.Document.EnumerateObject().Count() == 3 &&
                            System.Text.Encoding.UTF8.GetByteCount(item.Document.GetRawText()) < 160 && DatabaseWorkspaceCleanup.Read(item) is not null),
                            "Each database tombstone is a bounded three-ULID document at its mapped ULID key.");
                    }

                    cleanupQueries.Add(new(name, query.Filter, query.Project, query.Limit.Value, query.Cursor, page.Items.Count,
                        page.Items.Sum(item => System.Text.Encoding.UTF8.GetByteCount(item.Document.GetRawText()))));
                    return Results.Json(page);
                }
            }
        }
    }
}
