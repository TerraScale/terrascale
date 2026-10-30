using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Data
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class SchemaPreviewJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task PreviewPostsTheSameSchemaAndClearsStaleContinuationsWithoutWritingDocuments()
        {
            var calls = 0;
            const string schema = "{ \"type\": \"object\" }";
            var bodies = new List<string>();
            await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: async http =>
            {
                if (!http.Request.Path.Value!.EndsWith("/schema/preview", StringComparison.Ordinal))
                {
                    return null;
                }

                Interlocked.Increment(ref calls);
                bodies.Add(await new StreamReader(http.Request.Body).ReadToEndAsync());
                if (http.Request.Query["cursor"] == "opaque-continuation")
                {
                    return Results.StatusCode(409);
                }

                return Results.Json(new
                {
                    examined = 2,
                    valid = 1,
                    invalid = 1,
                    diagnostics = new[] { new { id_hash = new string('a', 64), path_hash = new string('b', 64), keyword = "type" } },
                    schema_hash = new string('c', 64),
                    frontier = new string('d', 64),
                    complete = false,
                    next_cursor = "opaque-continuation"
                });
            });
            using var client = host.CreateClient();
            await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
            { ["_csrf"] = csrf, ["name"] = "preview-data", ["region"] = "GRU" });
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var id = body.RootElement.GetProperty("id").GetString()!;
            var path = "/databases/" + id + "/schema/preview";
            var form = new Dictionary<string, string> { ["_csrf"] = csrf, ["collection"] = "users", ["schema"] = schema, ["cursor"] = "" };
            using (var first = await HttpJourney.PostFormAsync(client, path, form))
            {
                await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var html = await first.Content.ReadAsStringAsync();
                await Assert.That(html).Contains("This page: 2 examined").And.Contains("opaque-continuation").And.Contains("More documents remain");
            }
            form["cursor"] = "opaque-continuation";
            using (var stale = await HttpJourney.PostFormAsync(client, path, form))
            {
                await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
                var html = await stale.Content.ReadAsStringAsync();
                await Assert.That(html).Contains("preview is stale").And.DoesNotContain("Inspect next page").And.DoesNotContain("This page:");
            }
            await Assert.That(bodies.Count).IsEqualTo(2);
            using var expectedSchema = JsonDocument.Parse(schema);
            foreach (var submittedSchema in bodies)
            {
                using var actualSchema = JsonDocument.Parse(submittedSchema);
                await Assert.That(JsonElement.DeepEquals(actualSchema.RootElement, expectedSchema.RootElement)).IsTrue();
            }
            using (var foreign = await HttpJourney.PostFormAsync(client, "/databases/" + AccountService.NewId() + "/schema/preview", form))
            {
                await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }

            form["_csrf"] = "invalid";
            using (var refused = await HttpJourney.PostFormAsync(client, path, form))
            {
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            }

            using var controls = host.CreateProviderClient();
            using var role = await controls.PostAsJsonAsync("/__test/control", new { command = "role", value = "admin" });
            form["_csrf"] = csrf;
            using (var notOwner = await HttpJourney.PostFormAsync(client, path, form))
            {
                await Assert.That(notOwner.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            }

            await Assert.That(calls).IsEqualTo(2);
        }
    }
}
