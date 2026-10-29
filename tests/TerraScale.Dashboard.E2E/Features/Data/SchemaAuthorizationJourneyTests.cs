using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Data
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class SchemaAuthorizationJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task NativeSchemaReadUsesProjectAuthorityThroughOfficialDocumentsSdk()
        {
            var requests = 0;
            var schemaPath = "";
            await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
            {
                if (http.Request.Method != "GET" || !http.Request.Path.Value!.Contains("/schema/", StringComparison.Ordinal))
                {
                    return Task.FromResult<IResult?>(null);
                }
                // The producer fixture verifies the exchanged project's canonical
                // organization and identity before this extension is invoked.
                Interlocked.Increment(ref requests);
                schemaPath = http.Request.Path.Value;
                return Task.FromResult<IResult?>(Results.Json(new
                {
                    collection = "users",
                    documents = 1,
                    sampledDocuments = 1,
                    fields = new[] { new { path = "name", present = 1, types = new Dictionary<string, int> { ["string"] = 1 } } },
                    uniqueConstraints = Array.Empty<object>()
                }));
            });
            using var client = host.CreateClient();
            using var replica = host.CreateClient(replica: true);
            var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            using var login = await HttpJourney.LoginAsync(replica, account.Email, account.Password);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            using var identity = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
            var projectId = identity.RootElement.GetProperty("projectId").GetString()!;
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
            { ["_csrf"] = csrf, ["name"] = "schema-authority", ["region"] = "GRU" });
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            using var database = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var databaseId = database.RootElement.GetProperty("id").GetString()!;
            var path = "/api/v1/dashboard/schema?databaseId=" + databaseId + "&collection=users";
            using (var result = await replica.GetAsync(path))
            {
                await Assert.That(result.StatusCode).IsEqualTo(HttpStatusCode.OK);
                using var schema = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
                await Assert.That(schema.RootElement.GetProperty("collection").GetString()).IsEqualTo("users");
                await Assert.That(schema.RootElement.GetProperty("fields")[0].GetProperty("path").GetString()).IsEqualTo("name");
            }
            await Assert.That(schemaPath).IsEqualTo("/v1/projects/" + projectId + "/databases/schema-authority/schema/users");
            await Assert.That(requests).IsEqualTo(1);
            using (var foreign = await replica.GetAsync("/api/v1/dashboard/schema?databaseId=foreign&collection=users"))
            {
                await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }

            await Assert.That(requests).IsEqualTo(1);
            using var deleted = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/delete", new()
            { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["confirmName"] = "schema-authority" });
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }
    }
}
