using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TerraScale.Dashboard.E2E.Features.Data
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class SqlWorkspaceStateSdkJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task ErasureUsesScopedProjectedSdkPagesAndRetainsTerminalMarkersUntilOwnerErasure()
        {
            await using var scenario = await StateSdkScenario.PrepareAsync(fixture.Root);
            await Assert.That(await scenario.StartHostsAsync()).IsTrue();
            using var client = scenario.CreateClient();
            using var producer = scenario.CreateProviderClient();
            using var seeded = await client.PostAsync("/__test/sql-cleanup/seed", null);
            seeded.EnsureSuccessStatusCode();
            var scope = await seeded.Content.ReadFromJsonAsync<JsonElement>();
            var org = scope.GetProperty("org").GetString()!;
            var project = scope.GetProperty("project").GetString()!;
            var database = scope.GetProperty("database").GetString()!;
            var retained = scope.GetProperty("retained").EnumerateArray().Select(key => key.GetString()!).ToArray();
            var before = await SnapshotAsync();
            await Assert.That(Count(before, "dashboard_sql_workspaces")).IsEqualTo(70);
            await Assert.That(Count(before, "dashboard_sql_database_deletions")).IsEqualTo(37);
            var originals = new List<string>();
            foreach (var key in retained)
            {
                originals.Add(await WorkspaceAsync(key));
            }

            using (var erased = await client.PostAsync("/__test/sql-cleanup/replay", null))
            {
                erased.EnsureSuccessStatusCode();
            }

            var after = await SnapshotAsync();
            await Assert.That(Count(after, "dashboard_sql_workspaces")).IsEqualTo(3);
            await Assert.That(Count(after, "dashboard_sql_database_deletions")).IsEqualTo(37);
            for (var index = 0; index < retained.Length; index++)
            {
                await Assert.That(await WorkspaceAsync(retained[index])).IsEqualTo(originals[index]);
            }

            var queries = after.GetProperty("cleanupQueries").EnumerateArray().ToArray();
            var markers = queries.Where(query => query.GetProperty("collection").GetString() == "dashboard_sql_database_deletions").ToArray();
            await Assert.That(markers.Length).IsEqualTo(2);
            await Assert.That(markers[1].GetProperty("cursor").GetString()).IsNotNull();
            var workspaces = queries.Where(query => query.GetProperty("collection").GetString() == "dashboard_sql_workspaces").ToArray();
            var nonempty = workspaces.Where(query => query.GetProperty("items").GetInt32() > 0).ToArray();
            await Assert.That(nonempty.Select(query => query.GetProperty("items").GetInt32()).ToArray()).IsEquivalentTo(new[] { 32, 32, 3 });
            foreach (var query in workspaces)
            {
                await Assert.That(query.GetProperty("limit").GetInt32()).IsEqualTo(32);
                await Assert.That(query.GetProperty("bytes").GetInt32()).IsLessThan(8192);
                await Assert.That(query.GetProperty("fields").GetArrayLength()).IsEqualTo(4);
                var conditions = query.GetProperty("filter").GetProperty("and").EnumerateArray().Select(term => term.GetProperty("eq")).ToArray();
                await Assert.That(conditions.Length).IsEqualTo(3);
                await Assert.That(conditions.Select(eq => eq[0].GetString()!).ToArray()).IsEquivalentTo(new[] { "/OrganizationId", "/ProjectId", "/DatabaseId" });
                if (query.GetProperty("items").GetInt32() > 0)
                {
                    await Assert.That(conditions.Single(eq => eq[0].GetString() == "/OrganizationId")[1].GetString()).IsEqualTo(org);
                    await Assert.That(conditions.Single(eq => eq[0].GetString() == "/ProjectId")[1].GetString()).IsEqualTo(project);
                    await Assert.That(conditions.Single(eq => eq[0].GetString() == "/DatabaseId")[1].GetString()).IsEqualTo(database);
                }
            }
            await Assert.That(after.GetProperty("sqlWorkspaceLists").GetInt32()).IsEqualTo(0);
            using (var late = await client.PostAsync($"/__test/sql-cleanup/late/{org}/{project}/{database}", null))
            {
                late.EnsureSuccessStatusCode();
            }

            using (var fault = await producer.PostAsJsonAsync("/__test/control", new { command = "mode", value = "sql-delete-unavailable" }))
            {
                fault.EnsureSuccessStatusCode();
            }

            using (var failed = await client.PostAsync("/__test/sql-cleanup/replay", null))
            {
                await Assert.That(failed.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            }

            var interrupted = await SnapshotAsync();
            await Assert.That(Count(interrupted, "dashboard_sql_workspaces")).IsEqualTo(4);
            await Assert.That(Count(interrupted, "dashboard_sql_database_deletions")).IsEqualTo(37);
            using (var recovered = await producer.PostAsJsonAsync("/__test/control", new { command = "mode", value = "" }))
            {
                recovered.EnsureSuccessStatusCode();
            }

            using (var replay = await client.PostAsync("/__test/sql-cleanup/replay", null))
            {
                replay.EnsureSuccessStatusCode();
            }

            await Assert.That(Count(await SnapshotAsync(), "dashboard_sql_workspaces")).IsEqualTo(3);
            using (var erased = await client.PostAsync($"/__test/sql-cleanup/project/{org}/{project}", null))
            {
                erased.EnsureSuccessStatusCode();
            }

            await Assert.That(Count(await SnapshotAsync(), "dashboard_sql_database_deletions")).IsEqualTo(2);
            using (var erased = await client.PostAsync("/__test/sql-cleanup/organization/" + org, null))
            {
                erased.EnsureSuccessStatusCode();
            }

            var final = await SnapshotAsync();
            await Assert.That(Count(final, "dashboard_sql_database_deletions")).IsEqualTo(1);
            await Assert.That(final.GetProperty("failures").GetArrayLength()).IsEqualTo(0);

            async Task<JsonElement> SnapshotAsync() => await producer.GetFromJsonAsync<JsonElement>("/__test/observations");
            async Task<string> WorkspaceAsync(string key)
            {
                using var response = await client.GetAsync("/__test/sql-cleanup/workspace?key=" + Uri.EscapeDataString(key));
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
        }
        private static int Count(JsonElement snapshot, string collection)
        {
            return snapshot.GetProperty("collections").EnumerateArray()
            .Single(row => row.GetProperty("name").GetString() == collection).GetProperty("documents").GetInt32();
        }
    }
}
