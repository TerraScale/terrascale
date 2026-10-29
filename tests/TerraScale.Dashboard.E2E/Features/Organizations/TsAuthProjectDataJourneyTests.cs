using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Features.FileStorage;
using TerraScale.Dashboard.Features.Functions;
using TerraScale.Dashboard.Features.Importing;
using TerraScale.Dashboard.Features.Profile;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Features.Webhooks;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E.Features.Organizations
{
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
            var tokens = new WarmDataTokens();
            var wire = new DataWire { Tokens = tokens };
            AccountContext? stale = null;
            DataBackend? backend = null;
            var stateAccess = 0;
            var uploadToken = "";
            await using var platform = await PlatformScenario.StartAsync(builder =>
            {
                var fallback = (IHttpClientFactory)builder.Services.Last(item => item.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!;
                var inner = (IDashboardState)builder.Services.Last(item => item.ServiceType == typeof(IDashboardState)).ImplementationInstance!;
                builder.Services.Replace(ServiceDescriptor.Singleton<IDashboardState>(new DataState(inner, () => Interlocked.Increment(ref stateAccess))));
                wire.Fallback = fallback;
                builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(wire));
                builder.Services.Replace(ServiceDescriptor.Singleton<IProjectAccessTokens>(tokens));
            }, app => app.MapGet("/__test/data/{operation}", async Task<IResult> (string operation, HttpContext http, DashboardSessions sessions,
                AccountService accounts, DatabaseService databases, IDocumentBackend documents, IDatabaseBackend original, SavedQueryService saved,
                FileStorageService files, FunctionBackend functions, WebhookService hooks, ImportService imports) =>
            {
                try
                {
                    var context = operation == "capture" || stale is null ? (await AccountRequest.RequireAsync(http, sessions, accounts))! : stale;
                    if (operation == "capture") { stale = context; return Results.NoContent(); }
                    var id = http.Request.Query["database"].ToString();
                    var ct = http.RequestAborted;
                    var destination = http.Request.Query["destination"].FirstOrDefault() ?? "canonical-db";
                    if (operation == "foreign-org")
                    {
                        context = context with { Organization = new() { Id = AccountService.NewId() } };
                    }

                    if (operation == "foreign-project")
                    {
                        context = context with { Project = new() { Id = AccountService.NewId() } };
                    }

                    if (operation == "identity")
                    {
                        context = context with { Account = new() { UserId = "foreign" } };
                    }

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
                        case "import": uploadToken = await imports.AuthorizeUploadAsync(context, ct); break;
                        case "import-read": await imports.JobAsync(context, destination, ct); break;
                        case "import-upload": await imports.UploadAsync(context, destination, 0, new byte[] { 1 }, uploadToken, ct); break;
                        case "import-options": await imports.UpdateAsync(context, destination, [], ct); break;
                        case "import-cancel": await imports.CancelAsync(context, destination, ct); break;
                        case "import-resume": await imports.ResumeAsync(context, destination, ct); break;
                        case "import-start": await imports.StartAsync(context, destination, wire.PreviewId, true, ct); break;
                        case "file": await files.DeleteBucketAsync(context, wire.BucketId, "avatars", ct); break;
                        case "function": await functions.DeleteAsync(context, new() { Id = id, Name = "forged-name" }, "worker", ct); break;
                        case "function-read": await functions.ListAsync(context, DatabaseService.Database(context, id), ct); break;
                        case "webhook": await hooks.DeleteAsync(context, id, "items", AccountService.NewId(), ct); break;
                        case "webhook-read": await hooks.ListAsync(context, id, "items", ct); break;
                        case "list": return Results.Json(await saved.ListAsync(context, id, ct));
                        case "delete": await saved.DeleteAsync(context, id, http.Request.Query["query"].ToString(), ct); break;
                        default: return Results.Json(await saved.SaveAsync(context, id, "Shared " + Guid.NewGuid().ToString("N"), "SELECT 1", ct));
                    }
                    return Results.NoContent();
                }
                catch (AccountAccessException error) { return Results.StatusCode(error.StatusCode); }
                catch (HttpRequestException error) when (error.StatusCode is { } status) { return Results.StatusCode((int)status); }
            }));
            tokens.ProjectRole = (userId, orgId, projectId) => platform.Producer.ProjectRole(orgId, projectId, userId);
            var user = platform.Principal("data-" + role);
            var owner = platform.Principal("data-owner");
            using var client = platform.Client();
            using (var login = await HttpJourney.LoginAsync(client, user.Email, user.Password))
            {
                await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }

            var org = platform.Producer.AddOrganization("Project data", owner.Subject);
            var project = platform.Producer.AddProject(org, "Granted", owner.Subject);
            var sibling = platform.Producer.AddProject(org, "Sibling", owner.Subject);
            if (organizationViewer)
            {
                platform.Producer.AddMember(org, user.Subject, user.Email, "viewer");
            }

            platform.Producer.AddCollaborator(org, user.Subject, project, role);
            var id = AccountService.NewId();
            var otherId = AccountService.NewId();
            var projection = new OrganizationRecord { Id = org };
            foreach (var (projectId, databaseId) in new[] { (project, id), (sibling, otherId) })
            {
                projection.Projects[projectId] = new() { Id = projectId, Region = "GRU" };
                projection.Projects[projectId].Databases[databaseId] = new() { Id = databaseId, ProjectId = projectId, Name = projectId == project ? "canonical-db" : "sibling-db", Region = "GRU" };
                platform.SeedDatabase(new(databaseId, projectId, projection.Projects[projectId].Databases[databaseId].Name, DateTimeOffset.UtcNow, HomeRegion: "gru", ReplicaRegions: []));
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
            var writes = new[] { "collection", "document", "sql", "drop", "import", "import-upload", "import-options", "import-cancel", "import-resume", "import-start", "file", "function", "webhook", "save", "delete" };
            var allowed = role is "member" or "admin";
            foreach (var operation in writes.Where(item => item != "delete"))
            {
                await Check(operation, allowed ? operation == "save" ? HttpStatusCode.OK : HttpStatusCode.NoContent : HttpStatusCode.Forbidden);
            }

            await Check("read-sql", HttpStatusCode.NoContent);
            await Check("readonly-sql", HttpStatusCode.UnprocessableEntity);
            await Check("index", HttpStatusCode.Forbidden);
            await Check("index-drop", HttpStatusCode.Forbidden);
            await Check("function-read", allowed ? HttpStatusCode.NoContent : HttpStatusCode.Forbidden);
            await Assert.That(wire.Paths.Any(path => path.Contains("/functions", StringComparison.Ordinal))).IsEqualTo(allowed);
            await Check("import-read", HttpStatusCode.NoContent);
            await Assert.That(tokens.Scopes.Last()).IsEqualTo("databases:read");
            var importPath = $"/v1/projects/{project}/databases/canonical-db/import";
            await Assert.That(wire.Imports).IsEquivalentTo(allowed
                ? new[] { $"PUT {importPath}/source databases:write documents:write", $"GET {importPath} databases:read", $"PATCH {importPath} databases:write documents:write",
                    $"POST {importPath}/cancel databases:write", $"POST {importPath}/resume databases:write documents:write",
                    $"GET {importPath} databases:read", $"POST {importPath}/start databases:write documents:write", $"GET {importPath} databases:read" }
                : new[] { $"GET {importPath} databases:read" });
            await Check("webhook-read", HttpStatusCode.NoContent);
            await Assert.That(tokens.Scopes.Any(scopes => scopes == "documents:read")).IsTrue();
            await Assert.That(tokens.Scopes.Last()).IsEqualTo("documents:read");
            await Assert.That(tokens.Scopes.Contains("documents:read documents:write")).IsTrue(); // Function reads need both.
            await Assert.That(wire.Paths.Any(path => path.Contains("forged-name", StringComparison.Ordinal))).IsFalse();
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
            var dispatched = wire.Paths.Count;
            var sqlBackend = backend ?? throw new InvalidOperationException();
            var sqlCalls = sqlBackend.SqlCalls;
            var exchanges = tokens.Scopes.Count;
            var queryAccesses = stateAccess;
            foreach (var fault in new[] { "foreign-org", "foreign-project", "identity" })
            {
                await Check(fault, fault == "identity" ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound);
            }

            var goodScope = scope;
            scope = goodScope.Replace(id, otherId, StringComparison.Ordinal);
            foreach (var operation in new[] { "collection", "document", "sql", "drop", "function", "webhook", "save", "list", "delete" })
            {
                await Check(operation, role == "viewer" && (operation is "collection" or "document" or "drop" or "webhook") ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound);
            }

            scope = goodScope;
            foreach (var destination in new[] { "sibling-db", "missing-db", "invalid:name" })
            {
                foreach (var operation in new[] { "import-read", "import-upload", "import-options", "import-cancel", "import-resume", "import-start" })
                {
                    await Check(operation, allowed || operation == "import-read" || destination == "invalid:name" ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden, "&destination=" + destination);
                }
            }

            await Assert.That(wire.Paths.Count).IsEqualTo(dispatched);
            await Assert.That(sqlBackend.SqlCalls).IsEqualTo(sqlCalls);
            await Assert.That(stateAccess).IsEqualTo(queryAccesses);
            await Assert.That(tokens.Scopes.Count).IsEqualTo(exchanges);
            // Member/admin requests above acquired write grants; keep those cached while ts-auth demotes the user.
            await Assert.That(tokens.HasCachedWriteGrant).IsEqualTo(allowed);
            platform.Producer.AddCollaborator(org, user.Subject, project, "viewer");
            foreach (var operation in writes)
            {
                await Check(operation, HttpStatusCode.Forbidden, "&query=" + queryId);
            }

            await Check("list", HttpStatusCode.OK);
            await Assert.That(wire.Paths.Count).IsEqualTo(dispatched);
            await Assert.That(tokens.Scopes.Count).IsEqualTo(exchanges);
            platform.Producer.RemoveGrant(org, user.Subject, project);
            if (organizationViewer)
            {
                platform.Producer.RemoveMember(org, user.Subject);
            }

            foreach (var operation in writes.Append("list"))
            {
                await Check(operation, HttpStatusCode.NotFound);
            }

            await Assert.That(await platform.VersionAsync(org)).IsEqualTo(version);
            await Assert.That(platform.Producer.Mutations(org, user.Subject)).IsEqualTo(0);
            if (!organizationViewer)
            {
                await Assert.That(platform.Producer.PlanPublications(org)).IsEqualTo(0);
            }

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
            {
                await Assert.That(invalid.CanWriteProjectData).IsFalse();
            }

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

        // The persistence contract has internal primitives; a generated proxy assembly cannot implement them.
        private sealed class DataState(IDashboardState inner, Action accessed) : IDashboardState
        {
            private string Track(string collection) { if (collection == "dashboard_saved_queries") { accessed(); } return collection; }
            public Task InitializeAsync(CancellationToken ct)
            {
                return inner.InitializeAsync(ct);
            }

            public Task CheckReadyAsync(CancellationToken ct)
            {
                return inner.CheckReadyAsync(ct);
            }

            public Task<TerraBaseDocument?> GetAsync(string collection, string id, CancellationToken ct)
            {
                return inner.GetAsync(Track(collection), id, ct);
            }

            public Task<IReadOnlyList<TerraBaseDocument>> ListAsync(string collection, CancellationToken ct)
            {
                return inner.ListAsync(Track(collection), ct);
            }

            public Task<TerraBaseDocumentPage> QueryPageAsync(string collection, TerraBaseQuery query, CancellationToken ct)
            {
                return inner.QueryPageAsync(Track(collection), query, ct);
            }

            public Task PutAsync(string collection, string id, JsonElement document, TerraBasePrecondition precondition, CancellationToken ct)
            {
                return inner.PutAsync(Track(collection), id, document, precondition, ct);
            }

            public Task DeleteAsync(string collection, string id, string version, CancellationToken ct)
            {
                return inner.DeleteAsync(Track(collection), id, version, ct);
            }

            public Task DeleteListedAsync(string collection, TerraBaseDocument document, CancellationToken ct)
            {
                return inner.DeleteListedAsync(Track(collection), document, ct);
            }

            Task<TerraBaseDocumentPage> IDashboardState.ReadPageAsync(string collection, JsonElement? filter, int limit, string? cursor, CancellationToken ct)
            {
                return inner.ReadPageAsync(Track(collection), filter, limit, cursor, ct);
            }

            Task<TerraBaseTransactionResult> IDashboardState.TransactAsync(IReadOnlyList<DashboardStateOperation> operations, CancellationToken ct)
            {
                return inner.TransactAsync(operations, ct);
            }
        }

        private sealed class WarmDataTokens : IProjectAccessTokens
        {
            // Deliberately retains the first grant: guards must prevent dispatch after live-role changes.
            private readonly Dictionary<(string, string, string, string), string> cached = [];
            public List<string> Scopes { get; } = [];
            public Dictionary<string, (string Project, string Scope)> Grants { get; } = [];
            public Func<string, string, string, string?> ProjectRole { get; set; } = null!;
            public bool HasCachedWriteGrant => cached.Keys.Any(key => key.Item4.Contains(":write", StringComparison.Ordinal));
            public Task<string> GetAsync(SessionRecord session, string org, string project, IReadOnlyList<string> scopes, CancellationToken ct)
            {
                var scope = string.Join(' ', scopes.Order(StringComparer.Ordinal));
                var key = (session.AccessToken, org, project, scope);
                Scopes.Add(scope);
                if (!cached.ContainsKey(key))
                {
                    // PlatformGrants / TerraBaseScopes.For: cold grants use the producer's current effective role.
                    var role = ProjectRole(session.UserId, org, project);
                    if (role is not ("viewer" or "member" or "admin"))
                    {
                        throw new HttpRequestException("invalid_target", null, HttpStatusCode.NotFound);
                    }

                    string[] allowed = role == "viewer" ? ["databases:read", "documents:read", "shapes:read"]
                        : ["databases:read", "databases:write", "documents:read", "documents:write", "shapes:read"];
                    if (scopes.Except(allowed).Any())
                    {
                        throw new HttpRequestException("invalid_scope", null, HttpStatusCode.Forbidden);
                    }

                    cached[key] = "fixture-warmed-token-" + cached.Count;
                    Grants[cached[key]] = (project, scope);
                }
                return Task.FromResult(cached[key]);
            }
        }

        public class DataBackend : DispatchProxy
        {
            public IDatabaseBackend Inner { get; set; } = null!;
            public int SqlCalls { get; private set; }
            protected override object? Invoke(MethodInfo? method, object?[]? args)
            {
                if (method!.Name != nameof(IDatabaseBackend.SqlAsync))
                {
                    return method.Invoke(Inner, args);
                }

                SqlCalls++;
                return Task.FromResult(new DatabaseSqlResult([], [], 0, null));
            }
        }

        private sealed class DataWire : HttpMessageHandler, IHttpClientFactory
        {
            public IHttpClientFactory Fallback { get; set; } = null!;
            public WarmDataTokens Tokens { get; init; } = null!;
            public List<string> Imports { get; } = [];
            public string PreviewId { get; } = AccountService.NewId();
            public string BucketId { get; } = AccountService.NewId();
            public List<string> Paths { get; } = [];
            public HttpClient CreateClient(string name)
            {
                return new(new DataRouter(this, Fallback.CreateClient(name)));
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (request.Headers.Authorization?.Parameter is not { } token || !Tokens.Grants.TryGetValue(token, out var grant))
                {
                    throw new InvalidOperationException("Missing fixture grant.");
                }

                Paths.Add(request.RequestUri!.AbsolutePath);
                if (request.RequestUri.AbsolutePath.Contains("/import", StringComparison.Ordinal))
                {
                    if (request.RequestUri.Host != "gru.api.terrascale.tech" || request.Headers.GetValues("X-TerraScale-Region-Pin").Single() != "gru")
                    {
                        throw new InvalidOperationException("Import must use the destination home region and pin.");
                    }

                    var segments = request.RequestUri.AbsolutePath.Split('/');
                    if (grant.Project != segments[3])
                    {
                        throw new InvalidOperationException("Import token targets a different project.");
                    }

                    Imports.Add($"{request.Method} {request.RequestUri.AbsolutePath} {grant.Scope}");
                    if (request.Method == HttpMethod.Put && (request.RequestUri.Query != "?offset=0" || request.Content?.Headers.ContentLength != 1 ||
                        request.Content.Headers.ContentType?.MediaType != "application/octet-stream"))
                    {
                        throw new InvalidOperationException("The SDK must send the exact source part and offset.");
                    }
                    // Scripted producer wire reply: this journey checks routing and authority, not the import engine.
                    var body = JsonSerializer.Serialize(new
                    {
                        id = AccountService.NewId(),
                        project = segments[3],
                        database = segments[5],
                        region = "gru",
                        state = "ready",
                        cancelRequested = false,
                        options = new { exclude = Array.Empty<string>() },
                        source = new { format = "ndjson", bytes = 1, sha256 = new string('a', 64), receivedBytes = 1, staged = true, retainedUntil = (string?)null },
                        preview = new { id = PreviewId, tables = Array.Empty<object>(), skipped = Array.Empty<object>(), skippedTruncated = false, blocking = 0, warnings = 0 },
                        confirmedPreviewId = (string?)null,
                        progress = (object?)null,
                        validation = (object?)null,
                        error = (object?)null,
                        createdAt = DateTimeOffset.UtcNow,
                        updatedAt = DateTimeOffset.UtcNow,
                        startedAt = (string?)null,
                        finishedAt = (string?)null
                    });
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
                }
                if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.EndsWith("/buckets", StringComparison.Ordinal))
                {
                    var bucket = new TerraBaseFileBucket(BucketId, "avatars", "gru", new(null, new(false, false, []), new(false, false, [])));
                    var body = JsonSerializer.Serialize(new { items = new[] { bucket }, next = (string?)null });
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
                }
                return Task.FromResult(request.Method == HttpMethod.Get
                    ? new(HttpStatusCode.OK) { Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json") }
                    : new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            private sealed class DataRouter(DataWire owner, HttpClient fallback) : HttpMessageHandler
            {
                protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                {
                    return request.RequestUri!.Host.Contains("api.terrascale.tech", StringComparison.Ordinal) &&
                    request.RequestUri.AbsolutePath.StartsWith("/v1/projects/", StringComparison.Ordinal)
                        ? owner.SendAsync(request, ct) : HttpForwarding.SendAsync(fallback, request, ct);
                }

                protected override void Dispose(bool disposing) { if (disposing) { fallback.Dispose(); } base.Dispose(disposing); }
            }
        }
    }
}
