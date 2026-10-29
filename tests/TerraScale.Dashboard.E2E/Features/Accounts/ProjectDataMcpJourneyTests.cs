using System.Net;
using System.Text.Json;

namespace TerraScale.Dashboard.E2E.Features.Organizations
{
    public sealed partial class TsAuthPlatformJourneyTests
    {
        [Test]
        [Arguments("viewer")]
        [Arguments("member")]
        [Arguments("admin")]
        public async Task McpProfileAndDocumentWritesTrackTheLiveExactProjectRole(string role)
        {
            await using var platform = await PlatformScenario.StartAsync();
            var user = platform.Principal("mcp-data-" + role);
            var owner = platform.Principal("mcp-data-owner");
            using var browser = platform.Client();
            using var replica = platform.Client(replica: true);
            using (var login = await HttpJourney.LoginAsync(browser, user.Email, user.Password))
            {
                await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }

            var secret = await HttpJourney.CreatePatAsync(browser, password: user.Password);
            var original = (await McpCall(replica, secret, "get_context", new { })).GetProperty("selection");
            var org = platform.Producer.AddOrganization("MCP data workspace", owner.Subject);
            var project = platform.Producer.AddProject(org, "Granted data", owner.Subject);
            var sibling = platform.Producer.AddProject(org, "Denied data", owner.Subject);
            platform.Producer.AddCollaborator(org, user.Subject, project, role);
            var database = AccountService.NewId();
            var siblingDatabase = AccountService.NewId();
            var saved = new OrganizationRecord { Id = org };
            saved.Projects[project] = new() { Id = project, Region = "GRU" };
            saved.Projects[project].Databases[database] = new() { Id = database, ProjectId = project, Name = "data-db", Region = "GRU" };
            saved.Projects[sibling] = new() { Id = sibling, Region = "GRU" };
            saved.Projects[sibling].Databases[siblingDatabase] = new() { Id = siblingDatabase, ProjectId = sibling, Name = "private-db", Region = "GRU" };
            await platform.StoreAsync(saved);
            platform.SeedDatabase(new(database, project, "data-db", DateTimeOffset.UtcNow, HomeRegion: "gru", ReplicaRegions: []));

            var profile = await McpCall(replica, secret, "get_profile", new { organizationId = org, projectId = project });
            await CheckProfile(profile, role);
            await Assert.That(profile.GetProperty("projectId").GetString()).IsEqualTo(project);
            await Assert.That(profile.GetProperty("activeProjectId").GetString()).IsEqualTo(original.GetProperty("projectId").GetString());
            var context = await McpCall(replica, secret, "get_context", new { });
            await Assert.That(JsonElement.DeepEquals(context.GetProperty("selection"), original)).IsTrue();
            await McpDenied(replica, secret, "get_profile", new { organizationId = org, projectId = sibling });
            await McpCall(replica, secret, "select_project", new { projectId = project });
            await CheckProfile(await McpCall(replica, secret, "get_profile", new { }), role);

            // The discovery annotations and actual SQL path both stay read-only for every project role.
            var tools = (await HttpJourney.McpAsync(replica, secret, "tools/list", new { })).GetProperty("result").GetProperty("tools")
                .EnumerateArray().ToDictionary(item => item.GetProperty("name").GetString()!);
            foreach (var name in new[] { "execute_sql", "run_sql" })
            {
                await Assert.That(tools[name].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean()).IsTrue();
                await McpCall(replica, secret, name, new { databaseId = database, sql = "SELECT * FROM records LIMIT 10" });
                await McpDenied(replica, secret, name, new { databaseId = database, sql = "DELETE FROM records" },
                    "Only read-only statements (SELECT, WITH, EXPLAIN, VALUES, introspection PRAGMA) run here.");
            }
            await McpCall(replica, secret, "list_documents", new { databaseId = database, collection = "records" });
            await McpDenied(replica, secret, "list_documents", new { databaseId = siblingDatabase, collection = "records" });
            var document = AccountService.NewId();
            if (role == "viewer")
            {
                await McpDenied(replica, secret, "create_collection", new { databaseId = database, name = "records" });
                await McpDenied(replica, secret, "create_document", new { databaseId = database, collection = "records", documentId = document, document = new { value = 1 } }, "Write access is required.");
            }
            else
            {
                await McpCall(replica, secret, "create_collection", new { databaseId = database, name = "records" });
                await McpCall(replica, secret, "create_document", new { databaseId = database, collection = "records", documentId = document, document = new { value = 1 } });
                await McpCall(replica, secret, "replace_document", new { databaseId = database, collection = "records", documentId = document, document = new { value = 2 } });
                await McpCall(replica, secret, "patch_document", new { databaseId = database, collection = "records", documentId = document, patch = new { value = 3 } });
                // Reuse the warmed PAT on the same host immediately after the provider demotes it.
                platform.Producer.AddCollaborator(org, user.Subject, project, "viewer");
                await CheckProfile(await McpCall(replica, secret, "get_profile", new { }), "viewer");
                var current = await McpCall(replica, secret, "get_context", new { });
                var grant = current.GetProperty("organizations").EnumerateArray().Single(item => item.GetProperty("id").GetString() == org).GetProperty("projects")[0];
                await Assert.That(grant.GetProperty("role").GetString()).IsEqualTo("viewer");
                await Assert.That(grant.GetProperty("canWriteProjectData").GetBoolean()).IsFalse();
                var writes = new (string Name, object Arguments)[]
                {
                    ("create_document", new { documentId = AccountService.NewId(), document = new { value = 4 } }),
                    ("replace_document", new { documentId = document, document = new { value = 4 } }),
                    ("patch_document", new { documentId = document, patch = new { value = 4 } }),
                    ("delete_document", new { documentId = document })
                };
                foreach (var write in writes)
                {
                    var arguments = JsonSerializer.SerializeToElement(write.Arguments).EnumerateObject().ToDictionary(item => item.Name, item => (object?)item.Value.Clone());
                    arguments["databaseId"] = database;
                    arguments["collection"] = "records";
                    await McpDenied(replica, secret, write.Name, arguments, "Write access is required.");
                }
                var retained = await McpCall(replica, secret, "get_document", new { databaseId = database, collection = "records", documentId = document });
                await Assert.That(retained.GetProperty("value").GetProperty("value").GetInt32()).IsEqualTo(3);
                platform.Producer.AddCollaborator(org, user.Subject, project, role);
                await McpCall(replica, secret, "delete_document", new { databaseId = database, collection = "records", documentId = document });
            }
            platform.Producer.RemoveGrant(org, user.Subject, project);
            await McpDenied(replica, secret, "create_document", new { organizationId = org, projectId = project, databaseId = database, collection = "records", document = new { value = 5 } });
            await McpDenied(replica, secret, "list_documents", new { organizationId = org, projectId = project, databaseId = database, collection = "records" });
            await Assert.That(platform.Producer.Failures).IsEmpty();

            static async Task CheckProfile(JsonElement profile, string expected)
            {
                await Assert.That(profile.GetProperty("role").ValueKind).IsEqualTo(JsonValueKind.Null);
                await Assert.That(profile.GetProperty("projectRole").GetString()).IsEqualTo(expected);
                await Assert.That(profile.GetProperty("canWriteProjectData").GetBoolean()).IsEqualTo(expected != "viewer");
                await Assert.That(profile.GetProperty("permissions").EnumerateArray().Select(item => item.GetString()!).ToArray())
                    .IsEquivalentTo(expected == "viewer" ? new[] { "Access selected project" } : new[] { "Access selected project", "Write selected project data" });
            }
        }
    }
}
