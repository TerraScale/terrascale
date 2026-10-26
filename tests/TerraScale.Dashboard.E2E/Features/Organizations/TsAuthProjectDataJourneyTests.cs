using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Features.Profile;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E.Features.Organizations;

public sealed partial class TsAuthPlatformJourneyTests
{
    // Reuse the producer-shaped platform fixture, including higher(implied organization role, explicit grant).
    [Test]
    [Arguments("viewer", false)]
    [Arguments("member", false)]
    [Arguments("admin", false)]
    [Arguments("member", true)]
    public async Task ProjectDataWritesRefreshRolesWithoutOrganizationMutationAuthority(string role, bool organizationViewer)
    {
        AccountContext? stale = null;
        DataBackend? backend = null;
        var stateAccess = 0;
        await using var platform = await PlatformScenario.StartAsync(builder =>
        {
            var state = DispatchProxy.Create<IDashboardState, DataState>();
            ((DataState)state).Inner = (IDashboardState)builder.Services.Last(item => item.ServiceType == typeof(IDashboardState)).ImplementationInstance!;
            ((DataState)state).Accessed = () => Interlocked.Increment(ref stateAccess);
            builder.Services.Replace(ServiceDescriptor.Singleton(state));
        }, app => app.MapGet("/__test/data/{operation}", async Task<IResult> (string operation, HttpContext http, DashboardSessions sessions,
            AccountService accounts, DatabaseService databases, IDocumentBackend documents, IDatabaseBackend original, SavedQueryService saved) =>
        {
            try
            {
                var context = operation == "capture" || stale is null ? (await AccountRequest.RequireAsync(http, sessions, accounts))! : stale;
                if (operation == "capture") { stale = context; return Results.NoContent(); }
                var id = http.Request.Query["database"].ToString();
                var ct = http.RequestAborted;
                if (operation == "foreign-org") context = context with { Organization = new() { Id = AccountService.NewId() } };
                if (operation == "foreign-project") context = context with { Project = new() { Id = AccountService.NewId() } };
                if (operation == "identity") context = context with { Account = new() { UserId = "foreign" } };
                backend ??= DispatchProxy.Create<IDatabaseBackend, DataBackend>() as DataBackend;
                backend!.Inner = original;
                var data = new DataService(databases, documents, (IDatabaseBackend)backend);
                switch (operation)
                {
                    case "collection": await data.CreateCollectionAsync(context, id, "items", ct); break;
                    case "document": await data.WriteAsync(context, id, "items", "create", "", "{\"value\":1}", "", ct); break;
                    case "drop": await data.DropCollectionAsync(context, id, "items", "items", ct); break;
                    case "sql": await data.SqlAsync(context, id, "CREATE TABLE records (id TEXT)", true, ct); break;
                    case "read-sql": await data.SqlAsync(context, id, "SELECT * FROM records", false, ct); break;
                    case "readonly-sql": await data.SqlAsync(context, id, "DELETE FROM records", false, ct); break;
                    case "index": await data.CreateIndexAsync(context, id, "items", "value", ct); break;
                    case "index-drop": await data.DropIndexAsync(context, id, "value", ct); break;
                    case "list": return Results.Json(await saved.ListAsync(context, id, ct));
                    case "delete": await saved.DeleteAsync(context, id, http.Request.Query["query"].ToString(), ct); break;
                    default: return Results.Json(await saved.SaveAsync(context, id, "Shared", "SELECT 1", ct));
                }
                return Results.NoContent();
            }
            catch (AccountAccessException error) { return Results.StatusCode(error.StatusCode); }
        }));
        var user = platform.Principal("data-" + role);
        var owner = platform.Principal("data-owner");
        using var client = platform.Client();
        using (var login = await HttpJourney.LoginAsync(client, user.Email, user.Password))
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var org = platform.Producer.AddOrganization("Project data", owner.Subject);
        var project = platform.Producer.AddProject(org, "Granted", owner.Subject);
        var sibling = platform.Producer.AddProject(org, "Sibling", owner.Subject);
        if (organizationViewer) platform.Producer.AddMember(org, user.Subject, user.Email, "viewer");
        platform.Producer.AddCollaborator(org, user.Subject, project, role);
        var id = AccountService.NewId();
        var otherId = AccountService.NewId();
        var projection = new OrganizationRecord { Id = org };
        foreach (var (projectId, databaseId) in new[] { (project, id), (sibling, otherId) })
        {
            projection.Projects[projectId] = new() { Id = projectId, Region = "GRU" };
            projection.Projects[projectId].Databases[databaseId] = new() { Id = databaseId, ProjectId = projectId, Name = "canonical-db", Region = "GRU" };
            platform.SeedDatabase(new(databaseId, projectId, "canonical-db", DateTimeOffset.UtcNow, HomeRegion: "gru", ReplicaRegions: []));
        }
        await platform.StoreAsync(projection);
        var version = await platform.VersionAsync(org);
        var scope = "?organizationId=" + org + "&projectId=" + project + "&database=" + id;
        async Task Check(string operation, HttpStatusCode expected, string? query = null)
        {
            using var response = await client.GetAsync("/__test/data/" + operation + scope + query);
            await Assert.That(response.StatusCode).IsEqualTo(expected).Because(operation + ": " + await response.Content.ReadAsStringAsync());
        }
        await Check("capture", HttpStatusCode.NoContent);
        // Member reads publish the plan normally; that system write confers no organization mutation authority.
        await Assert.That(platform.Producer.PlanPublications(org) > 0).IsEqualTo(organizationViewer);
        var writes = new[] { "collection", "document", "sql", "drop", "save", "delete" };
        var allowed = role is "member" or "admin";
        foreach (var operation in writes.Where(item => item != "delete"))
            await Check(operation, allowed ? operation == "save" ? HttpStatusCode.OK : HttpStatusCode.NoContent : HttpStatusCode.Forbidden);
        await Check("read-sql", HttpStatusCode.NoContent);
        await Check("readonly-sql", HttpStatusCode.UnprocessableEntity);
        await Check("index", HttpStatusCode.Forbidden);
        await Check("index-drop", HttpStatusCode.Forbidden);
        var captured = stale ?? throw new InvalidOperationException();
        await Assert.That(captured.CanManage || captured.CanWrite || captured.Organization!.CanProvision(user.Subject)).IsFalse();
        await Assert.That(captured.CanWriteProjectData).IsEqualTo(allowed);
        await Assert.That(JsonSerializer.Serialize(captured.Project)).DoesNotContain("Role");
        using (var profileResponse = await client.GetAsync("/profile?organizationId=" + org + "&projectId=" + project))
        {
            await Assert.That(profileResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var profile = await profileResponse.Content.ReadAsStringAsync();
            var organizationRole = organizationViewer ? "viewer" : "No organization membership";
            await Assert.That(profile).Contains($"<dt class=\"ts-stat-label\">Scope roles</dt><dd class=\"ts-stat-value\">{(organizationViewer ? 2 : 1)}</dd>");
            await Assert.That(profile).Contains($"<dt>Active organization role</dt><dd><span class=\"ts-badge ts-badge-info\">{organizationRole}</span>");
            await Assert.That(profile).Contains($"<dt>Selected project role</dt><dd><span class=\"ts-badge ts-badge-info\">{role}</span>");
            await Assert.That(profile).Contains("Organization: " + organizationRole).And.Contains("Project: " + role);
            await Assert.That(profile.Contains("Write selected project data", StringComparison.Ordinal)).IsEqualTo(allowed);
            await Assert.That(profile).DoesNotContain("Manage organization resources").And.DoesNotContain("Provision organization resources");
        }
        var queryId = "";
        if (allowed)
        {
            using var listed = JsonDocument.Parse(await client.GetStringAsync("/__test/data/list" + scope));
            queryId = listed.RootElement[0].GetProperty("id").GetString()!;
            await Check("delete", HttpStatusCode.NoContent, "&query=" + queryId);
            await Task.WhenAll(Check("save", HttpStatusCode.OK), Check("save", HttpStatusCode.OK));
            using var concurrent = JsonDocument.Parse(await client.GetStringAsync("/__test/data/list" + scope));
            await Assert.That(concurrent.RootElement.GetArrayLength()).IsEqualTo(2).Because("project query CAS retains both writers");
            queryId = concurrent.RootElement[0].GetProperty("id").GetString()!;
            platform.Producer.AddCollaborator(org, user.Subject, sibling, "member");
            var ownScope = scope;
            scope = scope.Replace(project, sibling, StringComparison.Ordinal).Replace(id, otherId, StringComparison.Ordinal);
            await Check("capture", HttpStatusCode.NoContent);
            using var isolated = JsonDocument.Parse(await client.GetStringAsync("/__test/data/list" + scope));
            await Assert.That(isolated.RootElement.GetArrayLength()).IsEqualTo(0);
            await Check("delete", HttpStatusCode.NotFound, "&query=" + queryId);
            platform.Producer.RemoveGrant(org, user.Subject, sibling);
            scope = ownScope;
            await Check("capture", HttpStatusCode.NoContent);
        }
        var sqlBackend = backend ?? throw new InvalidOperationException();
        var sqlCalls = sqlBackend.SqlCalls;
        var queryAccesses = stateAccess;
        foreach (var fault in new[] { "foreign-org", "foreign-project", "identity" })
            await Check(fault, fault == "identity" ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound);
        var goodScope = scope;
        scope = goodScope.Replace(id, otherId, StringComparison.Ordinal);
        foreach (var operation in new[] { "collection", "document", "sql", "drop", "save", "list", "delete" })
            await Check(operation, role == "viewer" && (operation is "collection" or "document" or "drop") ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound);
        scope = goodScope;
        await Assert.That(sqlBackend.SqlCalls).IsEqualTo(sqlCalls);
        await Assert.That(stateAccess).IsEqualTo(queryAccesses);
        platform.Producer.AddCollaborator(org, user.Subject, project, "viewer");
        foreach (var operation in writes) await Check(operation, HttpStatusCode.Forbidden, "&query=" + queryId);
        await Check("list", HttpStatusCode.OK);
        platform.Producer.RemoveGrant(org, user.Subject, project);
        if (organizationViewer) platform.Producer.RemoveMember(org, user.Subject);
        foreach (var operation in writes.Append("list")) await Check(operation, HttpStatusCode.NotFound);
        await Assert.That(await platform.VersionAsync(org)).IsEqualTo(version);
        await Assert.That(platform.Producer.Mutations(org, user.Subject)).IsEqualTo(0);
        await Assert.That(platform.Producer.Failures).IsEmpty();
    }

    [Test]
    public async Task DataCapabilityRejectsNoncanonicalInactivePlatformAndUnauthenticatedContexts()
    {
        var user = AccountService.NewId();
        var org = new OrganizationRecord { Id = AccountService.NewId() };
        var project = new ProjectRecord { Id = AccountService.NewId(), OrganizationId = org.Id, Role = "member" };
        org.Projects[project.Id] = project;
        var context = new AccountContext(new(user, "Fixture", "fixture@example.test", "fixture-token", null, DateTimeOffset.UtcNow.AddHours(1)), new() { UserId = user }, [org], org, project);
        await Assert.That(context.CanWriteProjectData).IsTrue();
        foreach (var invalid in new[] { context with { Project = new() { Id = project.Id, OrganizationId = org.Id, Role = "member" } },
            context with { Organizations = [] }, context with { Account = new() { UserId = "other" } },
            context with { Session = context.Session with { AccessToken = "" } }, context with { Session = context.Session with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) } } })
            await Assert.That(invalid.CanWriteProjectData).IsFalse();
        foreach (var role in new[] { "viewer", "owner", "unknown", null }) { project.Role = role; await Assert.That(context.CanWriteProjectData).IsFalse(); }
        project.Role = "admin";
        foreach (var mutate in new Action[] { () => org.MfaRequired = true, () => org.Archived = true, () => org.Status = "deleted",
            () => project.Status = "deleted", () => project.OrganizationId = "other", () => org.Id = PlatformDatabases.OrganizationId })
        {
            org.MfaRequired = org.Archived = false; org.Status = project.Status = "active"; project.OrganizationId = org.Id;
            mutate(); await Assert.That(context.CanWriteProjectData).IsFalse();
        }
        project.OrganizationId = org.Id;
        var platformProfile = new ProfilePage(new(context, "Profile", "", "", ""));
        await Assert.That(platformProfile.SelectedProjectRole).IsEqualTo("viewer");
        await Assert.That(platformProfile.ScopeRoleCount).IsEqualTo(1);
        await Assert.That(platformProfile.OrganizationRoleLabel).IsEqualTo("No organization membership");
        await Assert.That(project.Role).IsEqualTo("admin").Because("display must not alter ts-auth authority");
    }

    public class DataState : DispatchProxy
    {
        public IDashboardState Inner { get; set; } = null!;
        public Action Accessed { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (args?.FirstOrDefault() is "dashboard_saved_queries") Accessed();
            try { return method!.Invoke(Inner, args); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
    }

    public class DataBackend : DispatchProxy
    {
        public IDatabaseBackend Inner { get; set; } = null!;
        public int SqlCalls { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name != nameof(IDatabaseBackend.SqlAsync)) return method.Invoke(Inner, args);
            SqlCalls++;
            return Task.FromResult(new DatabaseSqlResult([], [], 0, null));
        }
    }
}
