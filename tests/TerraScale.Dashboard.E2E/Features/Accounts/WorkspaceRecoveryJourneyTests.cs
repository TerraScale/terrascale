using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.E2E.Features.Databases;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Platform.Persistence;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E.Features.Accounts
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class WorkspaceRecoveryJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task CanonicalProjectIdentitySurvivesInterruptedSettingsWithoutDuplicateCreationOrRevokedMutation()
        {
            Func<string, (string UserId, string Email)?> resolveAccessToken = null!;
            var failureProjectId = "";
            var failureOrganizationId = "";
            var failureOwnerId = "";
            var unavailableDelete = 0;
            var unavailableDeleteRequests = 0;
            IDashboardState state = null!;
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root,
                builder =>
                {
                    resolveAccessToken = ((TestAuth)builder.Services.Last(item => item.ServiceType == typeof(IAuthGateway)).ImplementationInstance!).ResolveAccessToken;
                    state = (IDashboardState)builder.Services.Last(item => item.ServiceType == typeof(IDashboardState)).ImplementationInstance!;
                    var workspace = (TestWorkspace)builder.Services.Last(item => item.ServiceType == typeof(TestWorkspace)).ImplementationInstance!;
                    workspace.ProjectDeleteFailure = (session, organization, project) =>
                    {
                        if (project != failureProjectId || Volatile.Read(ref unavailableDelete) == 0)
                        {
                            return null;
                        }

                        if (resolveAccessToken(session.AccessToken)?.UserId != failureOwnerId || organization != failureOrganizationId)
                        {
                            throw new InvalidOperationException("The ts-auth dependency failure requires the exact current actor and tenant.");
                        }

                        Interlocked.Increment(ref unavailableDeleteRequests);
                        return new HttpRequestException("Test-only ts-auth project deletion interruption.", null, HttpStatusCode.ServiceUnavailable);
                    };
                });
            using var owner = scenario.CreateClient();
            owner.DefaultRequestHeaders.Accept.ParseAdd("text/html"); // Native browser form navigation.
            using var replica = scenario.CreateClient(replica: true);
            using var producer = scenario.CreateProviderClient();
            var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(owner);
            using var login = await HttpJourney.LoginAsync(replica, account.Email, account.Password);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var initial = await ContextAsync(owner);
            var organizationId = initial.GetProperty("selection").GetProperty("organizationId").GetString()!;
            var selectedProjectId = initial.GetProperty("selection").GetProperty("projectId").GetString()!;
            var selectedProject = Organization(initial, organizationId).GetProperty("projects").GetProperty(selectedProjectId);
            var csrf = await HttpJourney.CsrfAsync(owner, "/organization?tab=projects");
            using (var protectedProject = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/organization/projects/" + selectedProjectId + "/delete", new()
            { ["_csrf"] = csrf, ["confirmation"] = selectedProject.GetProperty("name").GetString()! }))
            {
                await Assert.That(protectedProject.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            }

            var name = "Interrupted SDK project " + Guid.NewGuid().ToString("N")[..12];
            var before = await ObservationsAsync(producer);
            // Test admission permits a second project; it does not simulate a paid checkout.
            await ControlAsync(producer, new { command = "plan", organizationId, value = OrganizationRecord.StarterPlanId });
            await ControlAsync(producer, new { command = "fail-project-metadata", organizationId, name });
            var form = new Dictionary<string, string> { ["_csrf"] = csrf, ["name"] = name, ["region"] = "GRU", ["regions"] = "GRU" };
            using (var interrupted = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/organization/projects", form))
            {
                await Assert.That(interrupted.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
                await Assert.That((await interrupted.Content.ReadAsStringAsync()).Contains("</html>", StringComparison.OrdinalIgnoreCase)).IsTrue();
            }
            var remote = await ObservationsAsync(producer);
            await Assert.That(remote.GetProperty("failedProjectMetadataWrites").GetInt32()).IsEqualTo(1);
            await Assert.That(remote.GetProperty("projectCreates").GetInt32()).IsEqualTo(before.GetProperty("projectCreates").GetInt32() + 1);
            var remoteProject = remote.GetProperty("workspaceProjects").EnumerateArray()
                .Single(project => project.GetProperty("organizationId").GetString() == organizationId && project.GetProperty("name").GetString() == name);
            var remoteId = remoteProject.GetProperty("id").GetString()!;
            var interruptedContext = await ContextAsync(replica);
            await Assert.That(Organization(interruptedContext, organizationId).GetProperty("projects").GetProperty(remoteId).GetProperty("name").GetString()).IsEqualTo(name);
            await Assert.That((await state.GetAsync(AccountService.OrganizationsCollection, organizationId, default))!.Document.GetProperty("Projects").TryGetProperty(remoteId, out _)).IsFalse()
                .Because("ts-auth identity remains visible while the interrupted dashboard settings write is absent.");
            var replicaCsrf = await HttpJourney.CsrfAsync(replica, "/organization?tab=projects");
            form["_csrf"] = replicaCsrf;
            await ControlAsync(producer, new { command = "role", value = "" });
            using (var removed = await HttpJourney.PostFormAsync(replica, "/api/v1/dashboard/organization/projects", form))
            {
                await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }

            var denied = await ObservationsAsync(producer);
            await Assert.That(denied.GetProperty("projectCreates").GetInt32()).IsEqualTo(remote.GetProperty("projectCreates").GetInt32());
            await Assert.That(denied.GetProperty("projectConflicts").GetInt32()).IsEqualTo(remote.GetProperty("projectConflicts").GetInt32());
            await ControlAsync(producer, new { command = "role", value = "owner" });
            using (var retried = await HttpJourney.PostFormAsync(replica, "/api/v1/dashboard/organization/projects", form))
            {
                await Assert.That(retried.StatusCode).IsEqualTo(HttpStatusCode.Conflict)
                    .Because("The canonical name already exists; replay never creates or claims a second project.");
            }
            using (var completedSettings = await HttpJourney.PostFormAsync(replica, "/api/v1/dashboard/organization/projects/" + remoteId + "/rename", form))
            {
                await Assert.That(completedSettings.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }

            var saved = await ContextAsync(owner);
            var project = Organization(saved, organizationId).GetProperty("projects").GetProperty(remoteId);
            await Assert.That(project.GetProperty("name").GetString()).IsEqualTo(name);
            await Assert.That(project.GetProperty("organizationId").GetString()).IsEqualTo(organizationId);
            await Assert.That(project.GetProperty("region").GetString()).IsEqualTo("GRU");
            await Assert.That(saved.GetProperty("selection").GetProperty("projectId").GetString()).IsEqualTo(selectedProjectId);
            var recovered = await ObservationsAsync(producer);
            await Assert.That(recovered.GetProperty("projectCreates").GetInt32()).IsEqualTo(remote.GetProperty("projectCreates").GetInt32());
            await Assert.That(recovered.GetProperty("projectConflicts").GetInt32()).IsEqualTo(remote.GetProperty("projectConflicts").GetInt32());
            await Assert.That((await state.GetAsync(AccountService.OrganizationsCollection, organizationId, default))!.Document.GetProperty("Projects").TryGetProperty(remoteId, out _)).IsTrue();
            using (var duplicate = await HttpJourney.PostFormAsync(replica, "/api/v1/dashboard/organization/projects", form))
            {
                await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            }

            using (var stillDefault = await HttpJourney.PostFormAsync(replica, "/api/v1/dashboard/organization/projects/" + selectedProjectId + "/delete", new()
            { ["_csrf"] = replicaCsrf, ["confirmation"] = selectedProject.GetProperty("name").GetString()! }))
            {
                await Assert.That(stillDefault.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            }

            var final = await ObservationsAsync(producer);
            await Assert.That(final.GetProperty("projectCreates").GetInt32()).IsEqualTo(recovered.GetProperty("projectCreates").GetInt32());
            await Assert.That(final.GetProperty("projectConflicts").GetInt32()).IsEqualTo(recovered.GetProperty("projectConflicts").GetInt32());
            await Assert.That(final.GetProperty("failures").GetArrayLength()).IsEqualTo(0);
            var ownerProjects = await owner.GetStringAsync("/organization?tab=projects");
            await Assert.That(ownerProjects.Contains("commandfor=\"rename-project-" + remoteId + "\"", StringComparison.Ordinal)).IsTrue();
            await ControlAsync(producer, new { command = "role", value = "admin" });
            var adminProjects = await replica.GetStringAsync("/organization?tab=projects");
            await Assert.That(adminProjects.Contains(remoteId, StringComparison.Ordinal)).IsTrue();
            await Assert.That(adminProjects.Contains(name, StringComparison.Ordinal)).IsTrue();
            await Assert.That(adminProjects.Contains("commandfor=\"rename-project-", StringComparison.Ordinal)).IsTrue();
            await Assert.That(adminProjects.Contains("commandfor=\"create-project\"", StringComparison.Ordinal)).IsFalse()
                .Because("Personal organization provisioning still requires its owner.");
            using (var adminRename = await HttpJourney.PostFormAsync(replica, "/api/v1/dashboard/organization/projects/" + remoteId + "/rename", new()
            { ["_csrf"] = replicaCsrf, ["name"] = "Admin recovered project", ["region"] = "GRU", ["backupRegion"] = "GRU", ["regions"] = "GRU" }))
            {
                await Assert.That(adminRename.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }

            await Assert.That(Organization(await ContextAsync(owner), organizationId).GetProperty("projects").GetProperty(remoteId).GetProperty("name").GetString()).IsEqualTo("Admin recovered project");
            using (var restoredName = await HttpJourney.PostFormAsync(replica, "/api/v1/dashboard/organization/projects/" + remoteId + "/rename", form))
            {
                await Assert.That(restoredName.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }

            await ControlAsync(producer, new { command = "role", value = "owner" });
            using (var selected = await HttpJourney.PostFormAsync(replica, "/api/v1/dashboard/switch-project", new()
            { ["_csrf"] = replicaCsrf, ["projectId"] = remoteId, ["returnUrl"] = "/organization?tab=projects" }))
            {
                await Assert.That(selected.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }

            using (var wrongConfirmation = await HttpJourney.PostFormAsync(replica, "/api/v1/dashboard/settings/delete-project", new()
            { ["_csrf"] = replicaCsrf, ["projectId"] = remoteId, ["confirmName"] = "Wrong project" }))
            {
                await Assert.That(wrongConfirmation.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            }

            failureOrganizationId = organizationId;
            failureOwnerId = initial.GetProperty("user").GetProperty("id").GetString()!;
            failureProjectId = remoteId;
            Volatile.Write(ref unavailableDelete, 1);
            using (var unavailableProvider = await HttpJourney.PostFormAsync(replica, "/api/v1/dashboard/settings/delete-project", new()
            { ["_csrf"] = replicaCsrf, ["projectId"] = remoteId, ["confirmName"] = name }))
            {
                await Assert.That(unavailableProvider.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            }

            Volatile.Write(ref unavailableDelete, 0);
            await Assert.That(Volatile.Read(ref unavailableDeleteRequests) > 0).IsTrue();
            var preserved = await ContextAsync(owner);
            await Assert.That(Organization(preserved, organizationId).GetProperty("projects").TryGetProperty(remoteId, out _)).IsTrue();
            var preservedRemote = await ObservationsAsync(producer);
            await Assert.That(preservedRemote.GetProperty("workspaceProjects").EnumerateArray().Any(project => project.GetProperty("id").GetString() == remoteId)).IsTrue();
            var beforeDelete = await ObservationsAsync(producer);
            await ControlAsync(producer, new { command = "fail-project-deletion-metadata", organizationId, projectId = remoteId });
            using (var interruptedDelete = await HttpJourney.PostFormAsync(replica, "/api/v1/dashboard/settings/delete-project", new()
            { ["_csrf"] = replicaCsrf, ["projectId"] = remoteId, ["confirmName"] = name }))
            {
                await Assert.That(interruptedDelete.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
                await Assert.That((await interruptedDelete.Content.ReadAsStringAsync()).Length > 0).IsTrue();
            }
            var interruptedDeletion = await ObservationsAsync(producer);
            await Assert.That(interruptedDeletion.GetProperty("projectDeletes").GetInt32()).IsEqualTo(beforeDelete.GetProperty("projectDeletes").GetInt32() + 1);
            await Assert.That(interruptedDeletion.GetProperty("failedProjectDeletionMetadataWrites").GetInt32()).IsEqualTo(1);
            await Assert.That(interruptedDeletion.GetProperty("workspaceProjects").EnumerateArray().Any(project => project.GetProperty("id").GetString() == remoteId)).IsFalse();
            var pendingCleanup = await ContextAsync(owner);
            await Assert.That(Organization(pendingCleanup, organizationId).GetProperty("projects").TryGetProperty(remoteId, out _)).IsFalse();
            await Assert.That(pendingCleanup.GetProperty("selection").GetProperty("projectId").ValueKind).IsEqualTo(JsonValueKind.Null);
            await Assert.That((await state.GetAsync(AccountService.OrganizationsCollection, organizationId, default))!.Document.GetProperty("Projects").TryGetProperty(remoteId, out _)).IsTrue()
                .Because("The interrupted settings removal cannot reintroduce a deleted canonical project.");
            await ControlAsync(producer, new { command = "role", value = "" });
            using (var revokedCleanup = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/settings/delete-project", new()
            { ["_csrf"] = csrf, ["projectId"] = remoteId, ["confirmName"] = name }))
            {
                await Assert.That(revokedCleanup.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }

            var revokedDelete = await ObservationsAsync(producer);
            await Assert.That(revokedDelete.GetProperty("projectDeletes").GetInt32()).IsEqualTo(interruptedDeletion.GetProperty("projectDeletes").GetInt32());
            await Assert.That(revokedDelete.GetProperty("projectDeleteNotFound").GetInt32()).IsEqualTo(interruptedDeletion.GetProperty("projectDeleteNotFound").GetInt32());
            await ControlAsync(producer, new { command = "role", value = "owner" });
            using (var deleted = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/settings/delete-project", new()
            { ["_csrf"] = csrf, ["projectId"] = remoteId, ["confirmName"] = name }))
            {
                await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }

            using (var updatedSettings = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/organization/projects/" + selectedProjectId + "/rename", new()
            { ["_csrf"] = csrf, ["name"] = selectedProject.GetProperty("name").GetString()!, ["region"] = "GRU", ["regions"] = "GRU" }))
            {
                await Assert.That(updatedSettings.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }

            await Assert.That((await state.GetAsync(AccountService.OrganizationsCollection, organizationId, default))!.Document.GetProperty("Projects").TryGetProperty(remoteId, out _)).IsFalse();
            using (var selectedDefault = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/switch-project", new()
            { ["_csrf"] = csrf, ["projectId"] = selectedProjectId, ["returnUrl"] = "/organization?tab=projects" }))
            {
                await Assert.That(selectedDefault.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }

            var cleaned = await ContextAsync(owner);
            await Assert.That(Organization(cleaned, organizationId).GetProperty("projects").TryGetProperty(remoteId, out _)).IsFalse();
            await Assert.That(cleaned.GetProperty("selection").GetProperty("projectId").GetString()).IsEqualTo(selectedProjectId);
            var cleanedReplica = await ContextAsync(replica);
            await Assert.That(Organization(cleanedReplica, organizationId).GetProperty("projects").TryGetProperty(remoteId, out _)).IsFalse();
            await Assert.That(cleanedReplica.GetProperty("selection").GetProperty("projectId").GetString()).IsEqualTo(selectedProjectId);
            var removedRemote = await ObservationsAsync(producer);
            await Assert.That(removedRemote.GetProperty("workspaceProjects").EnumerateArray().Any(project => project.GetProperty("id").GetString() == remoteId)).IsFalse();
            await Assert.That(removedRemote.GetProperty("projectDeletes").GetInt32()).IsEqualTo(interruptedDeletion.GetProperty("projectDeletes").GetInt32());
            await Assert.That(removedRemote.GetProperty("projectDeleteNotFound").GetInt32()).IsEqualTo(interruptedDeletion.GetProperty("projectDeleteNotFound").GetInt32());
            using (var alreadyRemoved = await HttpJourney.PostFormAsync(replica, "/api/v1/dashboard/settings/delete-project", new()
            { ["_csrf"] = replicaCsrf, ["projectId"] = remoteId, ["confirmName"] = name }))
            {
                await Assert.That(alreadyRemoved.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
                await Assert.That(await alreadyRemoved.Content.ReadAsStringAsync()).IsEqualTo("Enter the selected project name exactly to confirm deletion.");
            }
            var repeatedDelete = await ObservationsAsync(producer);
            await Assert.That(repeatedDelete.GetProperty("projectDeletes").GetInt32()).IsEqualTo(removedRemote.GetProperty("projectDeletes").GetInt32());
            await Assert.That(repeatedDelete.GetProperty("projectDeleteNotFound").GetInt32()).IsEqualTo(removedRemote.GetProperty("projectDeleteNotFound").GetInt32());
            await Assert.That(repeatedDelete.GetProperty("failures").GetArrayLength()).IsEqualTo(0);
            await Assert.That(removedRemote.GetProperty("failures").GetArrayLength()).IsEqualTo(0);
        }

        private static JsonElement Organization(JsonElement context, string id)
        {
            return context.GetProperty("organizations").EnumerateArray().Single(org => org.GetProperty("id").GetString() == id);
        }

        private static async Task<JsonElement> ContextAsync(HttpClient client)
        {
            using var response = await client.GetAsync("/api/v1/dashboard/context");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.Clone();
        }
        private static async Task ControlAsync(HttpClient client, object body)
        {
            using var response = await client.PostAsJsonAsync("/__test/control", body);
            response.EnsureSuccessStatusCode();
        }
        private static async Task<JsonElement> ObservationsAsync(HttpClient client)
        {
            using var response = await client.GetAsync("/__test/observations");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.Clone();
        }
    }
}
