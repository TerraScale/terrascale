using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TerraScale.Dashboard;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Features.Organizations;
using TerraScale.Dashboard.Features.Home;
using TerraScale.Dashboard.Features.Onboarding;
using TerraScale.Dashboard.Features.Profile;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E.Demo
{
    public sealed class DemoEnvironment
    {
        private readonly TestState state = new();
        private readonly TestAuth auth;
        private readonly TestIdentity identity = new();
        private readonly TestClock clock = new();
        private readonly TestWorkspace workspace = new();
        private readonly TestAuthentication authentication = new();
        private readonly TestDocuments documents = new();
        private readonly TestDatabases databases;
        private readonly TestStripeTransport stripe = new();
        private readonly TestBillingPlanTransport billingPlans;
        private readonly TestBillingCheckoutPersistence checkoutPersistence;
        private readonly TestReportingTransport reporting;
        private readonly TestAuthApiKeys apiKeys;
        private readonly TestImports imports;

        public DemoEnvironment()
        {
            auth = new(workspace.ProjectRole);
            databases = new TestDatabases(documents);
            billingPlans = new TestBillingPlanTransport(stripe);
            checkoutPersistence = new TestBillingCheckoutPersistence(state);
            apiKeys = new TestAuthApiKeys(auth.ResolveAccessToken, workspace.ProjectRole);
            reporting = new TestReportingTransport(state, workspace, auth.ResolveAccessToken, billingPlans, auth.ResolveProjectToken, apiKeys, documents);
            imports = new TestImports(reporting, databases, documents, auth.ResolveProjectToken);
        }

        internal TestWorkspace Workspace => workspace;
        internal TestBillingPlanTransport BillingPlans => billingPlans;
        internal TestAuthApiKeys ApiKeys => apiKeys;
        internal TestAuthentication Authentication => authentication;
        // The E2E suite seeds its reference fixtures through these.
        internal TestState State => state;
        internal TestAuth Auth => auth;
        internal TestDatabases Databases => databases;
        internal TestDocuments Documents => documents;

        public async Task InitializeAsync()
        {
            var personal = workspace.SeedPersonal(new("test-user", "Test User", "user@example.com", "", null, clock.GetUtcNow().AddHours(1)));
            await state.PutAsync(AccountService.OrganizationsCollection, personal.Id, JsonSerializer.SerializeToElement(personal), TerraBasePrecondition.Absent, default);
            var account = new AccountRecord
            {
                Id = AccountService.AccountId("test-user"),
                UserId = "test-user",
                Name = "Test User",
                Email = "user@example.com",
                OnboardingCompleted = true,
                OnboardingStep = "",
                OrganizationIds = [personal.Id],
                PersonalOrganizationId = personal.Id,
                ActiveOrganizationId = personal.Id,
                ActiveProjectId = personal.Projects.Keys.First(),
                CreatedAt = clock.GetUtcNow()
            };
            await state.PutAsync(AccountService.AccountsCollection, account.Id, JsonSerializer.SerializeToElement(account), TerraBasePrecondition.Absent, default);
        }

        // contentRoot holds web/static: the project directory, or the published application directory.
        public WebApplication CreateHost(string contentRoot, int port, string origin, Action<WebApplicationBuilder>? configureScenario = null)
        {
            var hostArgs = new[] { "--contentRoot", contentRoot, "--environment", "Development", "--urls", $"http://0.0.0.0:{port}" };
            stripe.SetOrigin(origin);
            var app = DashboardApplication.Build(hostArgs, builder =>
            {
                builder.Services.AddSingleton<IStartupFilter, TestClientAddress>();
                builder.Services.AddSingleton(stripe);
                builder.Services.Replace(ServiceDescriptor.Singleton<IDashboardState>(checkoutPersistence));
                builder.Services.Replace(ServiceDescriptor.Singleton<IAuthGateway>(auth));
                builder.Services.Replace(ServiceDescriptor.Singleton<IAuthRefreshGateway>(auth));
                builder.Services.Replace(ServiceDescriptor.Singleton<IAuthOidcGateway>(auth));
                builder.Services.Replace(ServiceDescriptor.Singleton<IAuthLogoutGateway>(auth));
                builder.Services.Replace(ServiceDescriptor.Singleton<IPlatformRoleGateway>(auth));
                builder.Services.Replace(ServiceDescriptor.Singleton<IProjectAccessTokens>(auth));
                builder.Services.Replace(ServiceDescriptor.Singleton<ITsAuthPlatform>(workspace));
                builder.Services.AddSingleton(workspace);
                builder.Services.Replace(ServiceDescriptor.Singleton<ITsAuthProjects>(authentication));
                builder.Services.Replace(ServiceDescriptor.Singleton<IDatabaseBackend>(databases));
                builder.Services.Replace(ServiceDescriptor.Singleton<IDocumentBackend>(documents));
                builder.Services.Replace(ServiceDescriptor.Singleton<IHomeMeasurements>(new TestMeasurements()));
                builder.Services.Replace(ServiceDescriptor.Singleton<TerraScale.Dashboard.Features.Notifications.IAllowanceNotificationBackend, TestAllowanceNotifications>());
                builder.Services.Replace(ServiceDescriptor.Singleton<TerraScale.Dashboard.Features.Support.ISupportMail, TestSupportMail>());
                // Test hosts report overage through UsageOverageReporter directly; the hourly worker would share the allowance feed.
                builder.Services.Remove(builder.Services.Single(service => service.ImplementationType == typeof(TerraScale.Dashboard.Features.Billing.UsageOverageWorker)));
                builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new TestHttpFactory(imports)));
                builder.Services.Replace(ServiceDescriptor.Singleton<IDashboardIdentity>(identity));
                builder.Services.Replace(ServiceDescriptor.Singleton<TimeProvider>(TimeProvider.System));
                // Scenarios run plan reconciliation on demand, so its timer never races their billing assertions.
                builder.Services.Remove(builder.Services.Single(service => service.ServiceType == typeof(IHostedService) && service.ImplementationType == typeof(PlanReconciler)));
                // Registration journeys run cleanup on demand, without racing the background timer.
                builder.Services.Remove(builder.Services.Single(service => service.ServiceType == typeof(IHostedService) && service.ImplementationType == typeof(TerraScale.Dashboard.Platform.Mcp.McpRegistrationReclaimer)));
                // Deletion journeys run the production consumer on demand against their isolated producer.
                builder.Services.Remove(builder.Services.Single(service => service.ServiceType == typeof(IHostedService) && service.ImplementationType == typeof(TerraScale.Dashboard.Features.Profile.DeletionReconciler)));
                builder.Logging.ClearProviders();
                builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);
                configureScenario?.Invoke(builder);
            });
            app.MapGet("/__test/checkout", (string id) => Results.Redirect(stripe.Complete(id)));
            // Test-only: report the signed-in organization's operation units now (or mark its pending overage invoiced), then list its overage items.
            app.MapPost("/__test/billing/overage", async (HttpContext http, DashboardSessions sessions, AccountService accounts, UsageOverageReporter reporter) =>
            {
                if ((await AccountRequest.RequireAsync(http, sessions, accounts))?.Organization is not { } organization)
                {
                    return Results.Unauthorized();
                }

                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                if (!Csrf.Validate(http, form))
                {
                    return Results.BadRequest();
                }

                if (form["invoice"] == "1")
                {
                    foreach (var item in stripe.OverageItems(organization.Id).Where(item => item.Invoice is null))
                    {
                        item.Invoice = "in_" + item.Id;
                    }
                }

                var now = DateTimeOffset.UtcNow;
                if (long.TryParse(form["units"], CultureInfo.InvariantCulture, out var units))
                {
                    await reporter.ReportAsync(organization.Id, units, new(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero), http.RequestAborted);
                }

                return Results.Json(stripe.OverageItems(organization.Id).Select(item => new
                {
                    item.Id,
                    item.Subscription,
                    item.Quantity,
                    item.UnitAmountDecimal,
                    item.Invoice,
                    item.Metadata,
                    PendingInvoiceItemInterval = stripe.PendingInvoiceItemIntervals.GetValueOrDefault(item.Subscription ?? "")
                }));
            });
            // Test-only stand-in for ts-auth's hosted sign-in and sign-up pages: the journey names the
            // account it signs in as. Without one the browser stops on the sign-in page, as it does at
            // ts-auth after logout, instead of silently signing an account back in.
            app.MapGet("/__test/oidc", (string? state, string? email, string? password, string? signup) => state is not { Length: 64 } || !state.All(char.IsAsciiHexDigitLower)
                ? Results.BadRequest("Invalid test-only OIDC state.")
                : email is null
                    ? Results.Content("<!doctype html><html lang=\"en\"><title>ts-auth sign-in</title><main><h1>Sign in</h1><p>Test-only stand-in for ts-auth's hosted sign-in.</p>" +
                        "<a href=\"/__test/oidc?state=" + state + "&amp;email=user%40example.com\">Continue as Test User</a></main></html>", "text/html")
                    : Results.Redirect("/login/callback?state=" + Uri.EscapeDataString(state) + "&" + auth.Authorize(email, password, signup == "1")));
            app.MapGet("/__test/portal", () => Results.Content("<h1>Test-only Stripe customer portal</h1><p>The official Stripe SDK issued this portal session.</p><a href='/organization?tab=billing'>Return to billing</a>", "text/html"));
            reporting.MapRoutes(app);
            imports.MapRoutes(app);
            billingPlans.MapRoutes(app);
            checkoutPersistence.MapRoutes(app);
            return app;
        }
    }

    public sealed record ReferencePrincipal(string Subject, string Name, string Email, string Password, string SourceAccountId = "", string Phone = "", string Locale = "en");

    sealed class TestIdentity : IDashboardIdentity
    {
        // A test-only peer credential: 64 hexadecimal characters, never a deployed value.
        public static readonly string PeerSecret = string.Concat(Enumerable.Repeat("7e57", 16));
        private readonly DashboardCredentials credentials = new("test-dashboard-client-secret-0123456789abcdef", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), new("sk_test_demo", "whsec_demo"), PeerSecret);
        public Task<DashboardCredentials> ReadAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(credentials);
        }
    }
    sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }

        public void Advance(TimeSpan delta)
        {
            now += delta;
        }
    }
    sealed class TestAuth(Func<string, string, string, string> projectRole) : IAuthGateway, IAuthOidcGateway, IAuthRefreshGateway, IAuthLogoutGateway, IPlatformRoleGateway, IProjectAccessTokens
    {
        private readonly TerraScale.DotNet.TerraScaleIdGenerator subjectIds = new();
        private readonly ConcurrentDictionary<string, string> passwords = new();
        private readonly ConcurrentDictionary<string, ReferencePrincipal> fixturePrincipals = new();
        private readonly ConcurrentDictionary<string, string> tokenOwners = new();
        private readonly ConcurrentDictionary<string, string> tokenFamilies = new();
        private readonly ConcurrentDictionary<string, TestRefresh> refreshGrants = new();
        private readonly ConcurrentDictionary<string, byte> consumedRefresh = new();
        private readonly ConcurrentDictionary<string, byte> revokedFamilies = new();
        private readonly ConcurrentDictionary<string, byte> revoked = new();
        private readonly ConcurrentDictionary<string, (string SessionToken, string OrganizationId, string ProjectId, string[] Scopes)> projectTokens = new();
        private static readonly string[] ProjectScopes = ["databases:read", "databases:write", "documents:read", "documents:write"];
        public ConcurrentQueue<TestProjectExchange> ProjectExchanges { get; } = new();
        public bool Active { get; set; } = true;
        public Exception? RevokeError { get; set; }
        public Exception? ExchangeError { get; set; }
        private readonly ConcurrentDictionary<string, SessionRecord> codes = new();
        public int SignIns { get; private set; }
        public int ActiveGrants => tokenOwners.Keys.Where(token => ResolveAccessToken(token) is not null).Select(token => tokenFamilies[token]).Distinct().Count();
        public void RegisterFixturePrincipal(ReferencePrincipal principal)
        {
            if (string.IsNullOrEmpty(principal.Subject) || string.IsNullOrEmpty(principal.Email) ||
                !fixturePrincipals.TryAdd(principal.Email, principal))
            {
                throw new InvalidDataException("Invalid or duplicate fixture principal.");
            }

            passwords[principal.Email] = principal.Password;
        }
        public (string UserId, string Email)? ResolveAccessToken(string token)
        {
            if (!Active || revoked.ContainsKey(token) || !tokenOwners.TryGetValue(token, out var email) ||
                !tokenFamilies.TryGetValue(token, out var family) || revokedFamilies.ContainsKey(family))
            {
                return null;
            }

            if (fixturePrincipals.TryGetValue(email, out var principal))
            {
                return (principal.Subject, principal.Email);
            }

            return (email == "user@example.com" ? "test-user" : "test-signup-" + email, email);
        }
        // The RFC 8693 exchange grants exactly the requested action scopes; a token outlives nothing its sign-in lost.
        public Task<string> GetAsync(SessionRecord session, string organizationId, string projectId, IReadOnlyList<string> scopes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ExchangeError is not null)
            {
                return Task.FromException<string>(ExchangeError);
            }

            if (ResolveAccessToken(session.AccessToken) is not { } actor || actor.UserId != session.UserId)
            {
                throw new HttpRequestException("Test project authority requires a live sign-in.", null, HttpStatusCode.Unauthorized);
            }

            if (scopes.Count == 0 || scopes.Any(scope => !ProjectScopes.Contains(scope)) || scopes.Distinct(StringComparer.Ordinal).Count() != scopes.Count)
            {
                throw new HttpRequestException("Test project authority grants only distinct databases:* and documents:* scopes.");
            }

            var role = projectRole(actor.UserId, organizationId, projectId);
            // The wire producer returns OAuth 400 invalid_target/invalid_scope; this fake implements I3's mapped gateway boundary.
            var status = role is not ("viewer" or "member" or "admin") ? HttpStatusCode.NotFound
                : role == "viewer" && scopes.Any(scope => scope.EndsWith(":write", StringComparison.Ordinal)) ? HttpStatusCode.Forbidden
                : HttpStatusCode.OK;
            ProjectExchanges.Enqueue(new(actor.UserId, organizationId, projectId, Array.AsReadOnly(scopes.ToArray()),
                Array.AsReadOnly(status == HttpStatusCode.OK ? scopes.ToArray() : []), status));
            if (status != HttpStatusCode.OK)
            {
                throw new HttpRequestException(status == HttpStatusCode.NotFound
                ? "The project is unavailable." : "The role does not grant the requested scopes.", null, status);
            }

            var token = "test-project-access-" + Guid.NewGuid().ToString("N");
            projectTokens[token] = (session.AccessToken, organizationId, projectId, scopes.ToArray());
            return Task.FromResult(token);
        }
        public (string UserId, string OrganizationId, string ProjectId, IReadOnlyList<string> Scopes)? ResolveProjectToken(string token)
        {
            return projectTokens.TryGetValue(token, out var grant) && ResolveAccessToken(grant.SessionToken) is { } actor
                ? (actor.UserId, grant.OrganizationId, grant.ProjectId, grant.Scopes) : null;
        }

        // The query a hosted sign-in answers the dashboard's callback with: a one-time code or access_denied.
        public string Authorize(string email, string? password, bool signUp)
        {
            if (signUp && !passwords.ContainsKey(email) && email != "user@example.com")
            {
                passwords[email] = password ?? "test-password";
                fixturePrincipals.GetOrAdd(email, address => new(subjectIds.NewId(), address, address, passwords[email]));
            }
            SignIns++;
            if (!Active || email != "user@example.com" && !passwords.ContainsKey(email) || (password ?? passwords.GetValueOrDefault(email, "test-password")) != passwords.GetValueOrDefault(email, "test-password"))
            {
                return "error=access_denied";
            }

            var session = fixturePrincipals.TryGetValue(email, out var principal)
                ? Issue(principal.Subject, principal.Name, principal.Email)
                : Issue(email == "user@example.com" ? "test-user" : "test-signup-" + email, email == "user@example.com" ? "Test User" : email, email);
            var code = "test-code-" + Guid.NewGuid().ToString("N");
            codes[code] = session;
            return "code=" + code;
        }
        // Changing a password happens on ts-auth's hosted account page; it ends every grant of that user.
        public void ChangePassword(string email, string password)
        {
            passwords[email] = password;
            foreach (var token in tokenOwners.Where(entry => entry.Value == email).Select(entry => entry.Key))
            {
                revoked[token] = 0;
                if (tokenFamilies.TryGetValue(token, out var family))
                {
                    revokedFamilies[family] = 0;
                }
            }
        }
        public Task<bool> IsActiveAsync(SessionRecord session, CancellationToken cancellationToken)
        {
            return Task.FromResult(ResolveAccessToken(session.AccessToken) is { } identity && identity.UserId == session.UserId && identity.Email == session.Email);
        }

        public Task RevokeAsync(SessionRecord session, CancellationToken cancellationToken)
        {
            if (RevokeError is not null)
            {
                return Task.FromException(RevokeError);
            }

            revoked[session.AccessToken] = 0;
            if (tokenFamilies.TryGetValue(session.AccessToken, out var family))
            {
                revokedFamilies[family] = 0;
            }

            return Task.CompletedTask;
        }
        public Task<SessionRecord> RefreshAsync(SessionRecord record, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Active || record.RefreshToken is not { } secret || !refreshGrants.TryGetValue(secret, out var previous) ||
                previous.Session.UserId != record.UserId || previous.Session.Email != record.Email || revokedFamilies.ContainsKey(previous.Family))
            {
                throw new SignInRejectedException();
            }

            if (!consumedRefresh.TryAdd(secret, 0))
            {
                revokedFamilies[previous.Family] = 0;
                throw new SignInRejectedException();
            }
            var fresh = Issue(record.UserId, record.Name, record.Email, previous.Family);
            return Task.FromResult(fresh with { SessionExpiresAt = record.SessionExpiresAt });
        }
        public Task<string?> PlatformRoleAsync(SessionRecord freshlyRotatedSession, CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>(null);
        }

        private SessionRecord Issue(string subject, string name, string email, string? family = null)
        {
            family ??= Guid.NewGuid().ToString("N");
            var access = "test-access-" + Guid.NewGuid().ToString("N");
            var refresh = "test-refresh-" + Guid.NewGuid().ToString("N");
            var session = new SessionRecord(subject, name, email, access, refresh, DateTimeOffset.UtcNow.AddHours(1)) { IdToken = "test-id-token-" + family };
            tokenOwners[access] = email;
            tokenFamilies[access] = family;
            refreshGrants[refresh] = new(session, family);
            return session;
        }
        private sealed record TestRefresh(SessionRecord Session, string Family);
        public Task<string> EndSessionUrlAsync(string dashboardOrigin, string? idToken, CancellationToken cancellationToken)
        {
            return Task.FromResult("/login");
        }

        public Task<string> AuthorizationUrlAsync(OidcFlow flow, CancellationToken cancellationToken)
        {
            return Task.FromResult("/__test/oidc?state=" + Uri.EscapeDataString(flow.State));
        }

        public Task<SessionRecord> ExchangeAsync(string code, OidcFlow flow, CancellationToken cancellationToken)
        {
            return codes.TryRemove(code, out var session)
                    ? Task.FromResult(session)
                    : Task.FromException<SessionRecord>(new SignInRejectedException());
        }
    }

    sealed record TestProjectExchange(string UserId, string OrganizationId, string ProjectId,
        IReadOnlyList<string> RequestedScopes, IReadOnlyList<string> GrantedScopes, HttpStatusCode Status);
    sealed class TestState : IDashboardState
    {
        public ConcurrentDictionary<string, TerraBaseDocument> Items { get; } = new();
        public Exception? ReadyError { get; set; }
        public Func<string, string, Task>? BeforeWriteAsync { get; set; }
        public Func<string, string, Exception?>? WriteFailure { get; set; }
        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task CheckReadyAsync(CancellationToken cancellationToken)
        {
            return ReadyError is null ? Task.CompletedTask : Task.FromException(ReadyError);
        }

        public Task<TerraBaseDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken)
        {
            return Task.FromResult(Items.GetValueOrDefault(collection + "/" + id));
        }

        Task<TerraBaseDocumentPage> IDashboardState.ReadPageAsync(string collection, JsonElement? filter, int limit, string? cursor, CancellationToken ct)
        {
            throw new NotSupportedException("Use StateSdkScenario for paged persistence journeys.");
        }

        Task<TerraBaseTransactionResult> IDashboardState.TransactAsync(IReadOnlyList<DashboardStateOperation> operations, CancellationToken ct)
        {
            throw new NotSupportedException("Use StateSdkScenario for atomic persistence journeys.");
        }

        public Task<IReadOnlyList<TerraBaseDocument>> ListAsync(string collection, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<TerraBaseDocument>>(Items.Where(item => item.Key.StartsWith(collection + "/", StringComparison.Ordinal)).Select(item => item.Value).ToArray());
        }

        public Task<TerraBaseDocumentPage> QueryPageAsync(string collection, TerraBaseQuery query, CancellationToken cancellationToken)
        {
            return Task.FromResult(TestStateQuery.Page(Items.Where(item => item.Key.StartsWith(collection + "/", StringComparison.Ordinal)).Select(item => item.Value), query));
        }

        public async Task PutAsync(string collection, string id, JsonElement document, TerraBasePrecondition precondition, CancellationToken cancellationToken)
        {
            if (BeforeWriteAsync is { } before)
            {
                await before(collection, id);
            }

            if (WriteFailure?.Invoke(collection, id) is { } error)
            {
                throw error;
            }

            lock (Items)
            {
                var key = collection + "/" + id;
                var existing = Items.GetValueOrDefault(key);
                var match = typeof(TerraBasePrecondition).GetField("ifMatch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(precondition) as EntityTagHeaderValue;
                if (ReferenceEquals(precondition, TerraBasePrecondition.Absent) && existing is not null ||
                    match is not null && (existing is null || match.Tag != "*" && match.Tag != "\"" + existing.Version + "\""))
                {
                    throw PreconditionFailed();
                }

                Items[key] = new(id, "tbv1-" + Guid.NewGuid().ToString("N"), "value", document.Clone());
            }
        }
        public Task DeleteAsync(string collection, string id, string version, CancellationToken cancellationToken)
        {
            var key = collection + "/" + id;
            lock (Items)
            {
                if (Items.GetValueOrDefault(key)?.Version != version)
                {
                    throw PreconditionFailed();
                }

                Items.TryRemove(key, out _);
            }
            return Task.CompletedTask;
        }
        private static Exception PreconditionFailed()
        {
            return (Exception)Activator.CreateInstance(typeof(TerraBasePreconditionFailedException), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [HttpStatusCode.PreconditionFailed, "Test version precondition failed", null, null], culture: null)!;
        }
    }

    // One provider registry per DemoEnvironment: its host pair shares it, other scenarios never do.
    sealed class TestWorkspace : ITsAuthPlatform
    {
        public Func<Task>? BeforeListMembers { get; set; }
        public Action? BeforeDeleteAccount { get; set; }
        public Func<SessionRecord, string, string, bool>? AuthorizeDeletion { get; set; }
        private int deletionCalls;
        public int DeletionCalls => Volatile.Read(ref deletionCalls);
        private readonly ConcurrentDictionary<string, bool> deletionReceipts = new();
        public void SetDeletionStatus(string receipt, bool completed)
        {
            deletionReceipts[receipt] = completed;
        }

        public readonly object Gate = new();
        public readonly Dictionary<string, OrganizationRecord> Organizations = [];
        private readonly Dictionary<string, (string OrganizationId, InvitationRecord Invitation)> invitations = [];
        private readonly Dictionary<string, IReadOnlyList<TerraScaleAuthPlatformInvitation>> capturedInvitations = [];
        private readonly Dictionary<string, List<TerraScaleAuthPlatformAuditEvent>> audit = [];
        private readonly Dictionary<string, TerraScaleAuthPlatformOwnershipTransfer> transfers = [];
        private readonly Dictionary<string, string> projectCreators = [];
        private readonly TerraScale.DotNet.TerraScaleIdGenerator ids = new();
        private string globalRole = "owner";
        public Func<SessionRecord, string, string, Exception?>? ProjectDeleteFailure { get; set; }
        // Root authorization tests deliberately override every role. Default owner
        // means use the scoped provider registry, never grant an arbitrary caller.
        public string Role { get => globalRole; set => globalRole = value; }
        public string GlobalRole { get => Role; set => Role = value; }
        public string ScopedRole(string userId, string organizationId)
        {
            lock (Gate)
            {
                var member = Organizations.GetValueOrDefault(organizationId)?.Members.GetValueOrDefault(userId);
                if (member is null || member.Status != "active")
                {
                    return "";
                }

                return globalRole != "owner" ? globalRole : member.Role;
            }
        }
        public string ProjectRole(string userId, string organizationId, string projectId)
        {
            lock (Gate)
            {
                var role = ScopedRole(userId, organizationId);
                if (role.Length == 0 || Organizations.GetValueOrDefault(organizationId)?.Projects.ContainsKey(projectId) != true)
                {
                    return "";
                }

                if (globalRole != "owner")
                {
                    return role;
                }

                return projectCreators.GetValueOrDefault(projectId) == userId || role is "owner" or "admin" ? "admin" : role;
            }
        }
        public static OrganizationRecord Clone(OrganizationRecord value)
        {
            // The fake auth registry retains live identity; dashboard persistence omits it.
            var copy = JsonSerializer.Deserialize<OrganizationRecord>(JsonSerializer.Serialize(value))!;
            copy.Name = value.Name;
            copy.Slug = value.Slug;
            copy.OwnerUserId = value.OwnerUserId;
            copy.PendingOwnerUserId = value.PendingOwnerUserId;
            copy.PendingOwnerExpiresAt = value.PendingOwnerExpiresAt;
            copy.CreatedAt = value.CreatedAt;
            copy.Members = JsonSerializer.Deserialize<Dictionary<string, MemberRecord>>(JsonSerializer.Serialize(value.Members))!;
            copy.Invitations = JsonSerializer.Deserialize<Dictionary<string, InvitationRecord>>(JsonSerializer.Serialize(value.Invitations))!;
            foreach (var (id, project) in value.Projects)
            {
                var target = copy.Projects[id];
                target.OrganizationId = project.OrganizationId;
                target.Name = project.Name;
                target.Slug = project.Slug;
                target.CreatedAt = project.CreatedAt;
            }
            return copy;
        }
        // The dashboard creates the Personal organization through ts-auth on first sign-in;
        // fixtures that start signed in seed the same shape directly.
        public OrganizationRecord SeedPersonal(SessionRecord session)
        {
            lock (Gate)
            {
                var org = Create(session, "Personal");
                var project = new ProjectRecord { Id = ids.NewId(), OrganizationId = org.Id, Name = "Personal", Slug = "personal", Region = "GRU", Regions = ["GRU"], IsDefault = true, CreatedAt = DateTimeOffset.UtcNow };
                org.Projects[project.Id] = project;
                projectCreators[project.Id] = session.UserId;
                var result = Clone(org);
                result.Kind = "personal";
                return result;
            }
        }
        public void ExpireInvitation(AccountContext context, string id, DateTimeOffset expiresAt)
        {
            lock (Gate)
            {
                if (ScopedRole(context.UserId, context.Organization!.Id) != "owner")
                {
                    throw new AccountAccessException("Organization ownership is required.");
                }

                if (!invitations.TryGetValue(id, out var value) || value.OrganizationId != context.Organization.Id)
                {
                    throw new AccountAccessException("Invitation is unavailable.", 404);
                }

                value.Invitation.ExpiresAt = expiresAt;
            }
        }
        public OrganizationRecord Get(string id)
        {
            return Organizations.GetValueOrDefault(id) ?? throw new AccountAccessException("Organization is unavailable.", 404);
        }

        public OrganizationRecord Manage(string userId, string id)
        {
            var org = Get(id);
            if (ScopedRole(userId, id) is not ("owner" or "admin"))
            {
                throw new AccountAccessException("Organization management is required.", 403, "forbidden_role");
            }

            return org;
        }
        private OrganizationRecord Member(string userId, string id)
        {
            var org = Get(id);
            if (ScopedRole(userId, id).Length == 0)
            {
                throw new AccountAccessException("Organization is unavailable.", 404, "not_found");
            }

            return org;
        }
        private OrganizationRecord Create(SessionRecord session, string name)
        {
            var org = new OrganizationRecord { Id = ids.NewId(), Name = name, Slug = AccountService.Slug(name), OwnerUserId = session.UserId, CreatedAt = DateTimeOffset.UtcNow };
            org.Members[session.UserId] = new() { UserId = session.UserId, Name = session.Name, Email = session.Email, Role = "owner", CreatedAt = org.CreatedAt };
            Organizations[org.Id] = org;
            return org;
        }
        private TerraScaleAuthPlatformOrganization Wire(OrganizationRecord org, string userId)
        {
            return new(org.Id, org.Name, org.OwnerUserId, org.CreatedAt, ScopedRole(userId, org.Id), false);
        }

        private TerraScaleAuthPlatformProject Wire(ProjectRecord project, OrganizationRecord org, string userId)
        {
            return new(project.Id, org.Id, project.Name, null, projectCreators.GetValueOrDefault(project.Id, org.OwnerUserId), project.CreatedAt, ProjectRole(userId, org.Id, project.Id), []);
        }

        private TerraScaleAuthPlatformInvitation Wire(InvitationRecord invitation, string invitedBy)
        {
            return new(invitation.Id, invitation.Email, invitation.Role, null, invitedBy, invitation.CreatedAt, invitation.ExpiresAt, invitation.ExpiresAt <= DateTimeOffset.UtcNow && invitation.Status == "pending" ? "expired" : invitation.Status);
        }

        private void LastOwner(OrganizationRecord org, string userId)
        {
            if (org.Members.Values.Count(member => member.Role == "owner" && member.Status == "active") == 1 && org.Members.GetValueOrDefault(userId)?.Role == "owner")
            {
                throw new AccountAccessException("The organization needs another owner first.", 409, "last_owner");
            }
        }

        public Task<IReadOnlyList<TerraScaleAuthPlatformOrganization>> ListOrganizationsAsync(SessionRecord session, CancellationToken cancellationToken)
        { lock (Gate)
            {
                return Task.FromResult<IReadOnlyList<TerraScaleAuthPlatformOrganization>>(Organizations.Values.Where(org => ScopedRole(session.UserId, org.Id).Length > 0).Select(org => Wire(org, session.UserId)).ToArray());
            }
        }
        public Task<TerraScaleAuthPlatformOrganization> CreateOrganizationAsync(SessionRecord session, string name, CancellationToken cancellationToken)
        { lock (Gate)
            {
                return Task.FromResult(Wire(Create(session, name), session.UserId));
            }
        }
        public Task RenameOrganizationAsync(SessionRecord session, string organizationId, string name, CancellationToken cancellationToken)
        { lock (Gate) { var org = Manage(session.UserId, organizationId); org.Name = name; org.Slug = AccountService.Slug(name); return Task.CompletedTask; } }
        public Task LeaveOrganizationAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
        { lock (Gate) { var org = Member(session.UserId, organizationId); LastOwner(org, session.UserId); org.Members.Remove(session.UserId); return Task.CompletedTask; } }
        public Task SignOutEverywhereAsync(SessionRecord session, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task DeleteOrganizationAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
        {
            lock (Gate)
            {
                var org = Member(session.UserId, organizationId);
                if (ScopedRole(session.UserId, organizationId) != "owner")
                {
                    throw new AccountAccessException("Your role does not allow this change.", 403, "forbidden_role");
                }

                if (org.Projects.Count > 0)
                {
                    throw new AccountAccessException("Delete the organization's projects first.", 409, "not_empty");
                }

                Organizations.Remove(organizationId);
                return Task.CompletedTask;
            }
        }
        public async Task<IReadOnlyList<TerraScaleAuthPlatformMember>> ListMembersAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
        {
            if (BeforeListMembers is not null)
            {
                await BeforeListMembers();
            }

            lock (Gate)
            {
                return Member(session.UserId, organizationId).Members.Values.Where(member => member.Status == "active").Select(member => new TerraScaleAuthPlatformMember(member.UserId, member.Email, ScopedRole(member.UserId, organizationId), member.CreatedAt)).ToArray();
            }
        }
        public Task ChangeMemberRoleAsync(SessionRecord session, string organizationId, string userId, string role, CancellationToken cancellationToken)
        {
            lock (Gate)
            {
                var org = Manage(session.UserId, organizationId);
                if (role is not ("viewer" or "member" or "admin" or "owner"))
                {
                    throw new AccountAccessException("role is invalid.", 422, "invalid_request");
                }

                var member = org.Members.GetValueOrDefault(userId) ?? throw new AccountAccessException("Member is unavailable.", 404, "not_found");
                if (role != "owner")
                {
                    LastOwner(org, userId);
                }

                member.Role = role;
                return Task.CompletedTask;
            }
        }
        public Task RemoveMemberAsync(SessionRecord session, string organizationId, string userId, CancellationToken cancellationToken)
        { lock (Gate) { var org = Manage(session.UserId, organizationId); if (!org.Members.ContainsKey(userId)) { throw new AccountAccessException("Member is unavailable.", 404, "not_found"); } LastOwner(org, userId); org.Members.Remove(userId); return Task.CompletedTask; } }
        public Task<TerraScaleAuthPlatformProjectMember> SetProjectMemberRoleAsync(SessionRecord session, string organizationId, string projectId, string userId, string role, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("Use PlatformScenario for project-grant transport journeys.");
        }

        public Task RemoveProjectMemberAsync(SessionRecord session, string organizationId, string projectId, string userId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("Use PlatformScenario for project-grant transport journeys.");
        }

        public Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformInvitation>> ListProjectInvitationsAsync(SessionRecord session, string organizationId, string projectId, string? after, CancellationToken cancellationToken)
        {
            throw new AccountAccessException("Project invitations require ts-auth authorization.", 503);
        }

        public Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformProjectMember>> ListProjectMembersAsync(SessionRecord session, string organizationId, string projectId, string? after, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("Use PlatformScenario for project-grant transport journeys.");
        }

        public Task<IReadOnlyList<TerraScaleAuthPlatformProject>> ListProjectsAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
        { lock (Gate) { var org = Member(session.UserId, organizationId); return Task.FromResult<IReadOnlyList<TerraScaleAuthPlatformProject>>(org.Projects.Values.Select(project => Wire(project, org, session.UserId)).ToArray()); } }
        public Task<TerraScaleAuthPlatformProject> CreateProjectAsync(SessionRecord session, string organizationId, string name, CancellationToken cancellationToken)
        {
            lock (Gate)
            {
                var org = Member(session.UserId, organizationId);
                if (ScopedRole(session.UserId, organizationId) is not ("owner" or "admin" or "member"))
                {
                    throw new AccountAccessException("Project provisioning is forbidden.", 403, "forbidden_role");
                }

                if (org.Projects.Values.Any(project => project.Name == name))
                {
                    throw new AccountAccessException("A project with that name already exists.", 409, "name_taken");
                }

                var project = new ProjectRecord { Id = ids.NewId(), OrganizationId = organizationId, Name = name, Slug = AccountService.Slug(name), CreatedAt = DateTimeOffset.UtcNow };
                org.Projects[project.Id] = project;
                projectCreators[project.Id] = session.UserId;
                return Task.FromResult(Wire(project, org, session.UserId));
            }
        }
        public Task RenameProjectAsync(SessionRecord session, string organizationId, string projectId, string name, CancellationToken cancellationToken)
        {
            lock (Gate)
            {
                var org = Manage(session.UserId, organizationId);
                var project = org.Projects.GetValueOrDefault(projectId) ?? throw new AccountAccessException("Project is unavailable.", 404, "not_found");
                if (org.Projects.Values.Any(other => other.Id != projectId && other.Name == name))
                {
                    throw new AccountAccessException("A project with that name already exists.", 409, "name_taken");
                }

                project.Name = name; project.Slug = AccountService.Slug(name);
                return Task.CompletedTask;
            }
        }
        public Task<bool> DeleteProjectAsync(SessionRecord session, string organizationId, string projectId, CancellationToken cancellationToken)
        { lock (Gate) { var org = Manage(session.UserId, organizationId); if (ProjectDeleteFailure?.Invoke(session, organizationId, projectId) is { } error) { throw error; } if (!org.Projects.Remove(projectId)) { throw new AccountAccessException("Project is unavailable.", 404, "not_found"); } return Task.FromResult(true); } }
        public Task<IReadOnlyList<TerraScaleAuthPlatformInvitation>> ListInvitationsAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
        { lock (Gate) { var org = Manage(session.UserId, organizationId); return Task.FromResult<IReadOnlyList<TerraScaleAuthPlatformInvitation>>(invitations.Values.Where(value => value.OrganizationId == org.Id).Select(value => Wire(value.Invitation, org.OwnerUserId)).Concat(capturedInvitations.GetValueOrDefault(org.Id) ?? []).ToArray()); } }
        public void SeedInvitationMetadata(OrganizationRecord organization)
        {
            // Historical inventory has no invitation grant or inferred expiry timestamp.
            lock (Gate)
            {
                capturedInvitations[organization.Id] = organization.Invitations.Values.Select(invitation =>
                new TerraScaleAuthPlatformInvitation(invitation.Id, invitation.Email, invitation.Role, null, organization.OwnerUserId,
                    invitation.CreatedAt, invitation.ExpiresAt, invitation.Status)).ToArray();
            }
        }
        public Task<TerraScaleAuthPlatformCreatedInvitation> CreateInvitationAsync(SessionRecord session, string organizationId, string email, string role, CancellationToken cancellationToken)
        {
            lock (Gate)
            {
                var org = Manage(session.UserId, organizationId);
                if (org.Members.Values.Any(member => member.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new AccountAccessException("Already a member.", 409, "already_member");
                }

                if (invitations.Values.Any(value => value.OrganizationId == org.Id && value.Invitation.Status == "pending" && value.Invitation.ExpiresAt > DateTimeOffset.UtcNow &&
                    value.Invitation.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new AccountAccessException("An invitation is pending.", 409, "invitation_pending");
                }

                var invitation = new InvitationRecord { Id = ids.NewId(), OrganizationId = org.Id, Email = email, Role = role, Status = "pending", CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) };
                invitations[invitation.Id] = (org.Id, invitation);
                InvitationAudit(org.Id, session.UserId, "invitation.created", email, role);
                return Task.FromResult(new TerraScaleAuthPlatformCreatedInvitation(Wire(invitation, session.UserId), "test-invitation-" + invitation.Id));
            }
        }
        public Task RevokeInvitationAsync(SessionRecord session, string organizationId, string invitationId, CancellationToken cancellationToken)
        {
            lock (Gate)
            {
                Manage(session.UserId, organizationId);
                if (!invitations.TryGetValue(invitationId, out var value) || value.OrganizationId != organizationId || value.Invitation.Status != "pending")
                {
                    throw new AccountAccessException("Invitation is unavailable.", 404, "not_found");
                }

                value.Invitation.Status = "revoked";
                InvitationAudit(organizationId, session.UserId, "invitation.revoked", value.Invitation.Email);
                return Task.CompletedTask;
            }
        }
        private (string OrganizationId, InvitationRecord Invitation) Pending(string token)
        {
            const string prefix = "test-invitation-";
            if (!token.StartsWith(prefix, StringComparison.Ordinal) || !invitations.TryGetValue(token[prefix.Length..], out var found) ||
                found.Invitation.Status != "pending" || found.Invitation.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                throw new AccountAccessException("Invitation is unavailable.", 404, "not_found");
            }

            return found;
        }
        public Task<TerraScaleAuthPlatformInvitationLookup?> LookupInvitationAsync(SessionRecord session, string token, CancellationToken cancellationToken)
        {
            lock (Gate)
            {
                try
                {
                    var found = Pending(token);
                    return Task.FromResult<TerraScaleAuthPlatformInvitationLookup?>(new(new(found.OrganizationId, Get(found.OrganizationId).Name), null, found.Invitation.Email, found.Invitation.Role, found.Invitation.ExpiresAt));
                }
                catch (AccountAccessException) { return Task.FromResult<TerraScaleAuthPlatformInvitationLookup?>(null); }
            }
        }
        public Task<TerraScaleAuthPlatformAcceptedInvitation> AcceptInvitationAsync(SessionRecord session, string token, CancellationToken cancellationToken)
        {
            lock (Gate)
            {
                var found = Pending(token);
                if (!found.Invitation.Email.Equals(session.Email, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AccountAccessException("Sign in with the invited email.", 403, "email_mismatch");
                }

                var org = Get(found.OrganizationId);
                if (org.Members.ContainsKey(session.UserId))
                {
                    throw new AccountAccessException("Already a member.", 409, "already_member");
                }

                org.Members[session.UserId] = new() { UserId = session.UserId, Name = session.Name, Email = session.Email, Role = found.Invitation.Role, CreatedAt = DateTimeOffset.UtcNow };
                found.Invitation.Status = "accepted";
                InvitationAudit(org.Id, session.UserId, "member.joined", session.UserId, found.Invitation.Role);
                InvitationAudit(org.Id, session.UserId, "invitation.accepted", found.Invitation.Email);
                return Task.FromResult(new TerraScaleAuthPlatformAcceptedInvitation(org.Id, null, found.Invitation.Role));
            }
        }
        public Task DeclineInvitationAsync(SessionRecord session, string token, CancellationToken cancellationToken)
        {
            lock (Gate)
            {
                var found = Pending(token);
                if (!found.Invitation.Email.Equals(session.Email, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AccountAccessException("Sign in with the invited email.", 403, "email_mismatch");
                }

                found.Invitation.Status = "declined";
                InvitationAudit(found.OrganizationId, session.UserId, "invitation.declined", found.Invitation.Email);
                return Task.CompletedTask;
            }
        }
        public Task<TerraScaleAuthPlatformOwnershipTransfer?> GetOwnershipTransferAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
        { lock (Gate) { Member(session.UserId, organizationId); return Task.FromResult(transfers.GetValueOrDefault(organizationId)); } }
        public Task StartOwnershipTransferAsync(SessionRecord session, string organizationId, string userId, CancellationToken cancellationToken)
        {
            lock (Gate)
            {
                var org = Member(session.UserId, organizationId);
                if (ScopedRole(session.UserId, organizationId) != "owner")
                {
                    throw new AccountAccessException("Organization ownership is required.", 403, "forbidden_role");
                }

                if (!org.Members.TryGetValue(userId, out var nominee) || nominee.Role == "owner")
                {
                    throw new AccountAccessException("Choose a member who is not an owner.", 422, "invalid_request");
                }

                transfers[org.Id] = new(session.UserId, userId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7));
                return Task.CompletedTask;
            }
        }
        public Task ConfirmOwnershipTransferAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
        {
            lock (Gate)
            {
                var org = Member(session.UserId, organizationId);
                var transfer = transfers.GetValueOrDefault(org.Id) ?? throw new AccountAccessException("Ownership transfer is unavailable.", 404, "not_found");
                if (transfer.ToUserId != session.UserId || transfer.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    throw new AccountAccessException("Only the nominee can confirm.", 403, "forbidden_role");
                }

                org.Members[transfer.ToUserId].Role = "owner";
                if (org.Members.TryGetValue(transfer.FromUserId, out var previous))
                {
                    previous.Role = "admin";
                }

                org.OwnerUserId = transfer.ToUserId;
                transfers.Remove(org.Id);
                return Task.CompletedTask;
            }
        }
        public Task CancelOwnershipTransferAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
        {
            lock (Gate)
            {
                Member(session.UserId, organizationId);
                var transfer = transfers.GetValueOrDefault(organizationId) ?? throw new AccountAccessException("Ownership transfer is unavailable.", 404, "not_found");
                if (ScopedRole(session.UserId, organizationId) != "owner" && transfer.ToUserId != session.UserId)
                {
                    throw new AccountAccessException("Ownership transfer cancellation is forbidden.", 403, "forbidden_role");
                }

                transfers.Remove(organizationId);
                return Task.CompletedTask;
            }
        }
        public Task<IReadOnlyList<TerraScaleAuthPlatformAuditEvent>> ListAuditEventsAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
        {
            lock (Gate)
            {
                Manage(session.UserId, organizationId);
                return Task.FromResult<IReadOnlyList<TerraScaleAuthPlatformAuditEvent>>((audit.GetValueOrDefault(organizationId) ?? [])
                    .OrderByDescending(item => item.OccurredAt).ThenByDescending(item => item.Id, StringComparer.Ordinal).ToArray());
            }
        }
        // ts-auth DELETE /v1/terrascale/account: refuses last_owner of a shared organization, deletes sole-member organizations, leaves the rest.
        public Task<bool> AccountDeletionOrganizationCompletedAsync(string receipt, string organizationId, CancellationToken cancellationToken)
        {
            return Task.FromResult(!Organizations.ContainsKey(organizationId));
        }

        public Task<bool> AccountDeletionCompletedAsync(string receipt, CancellationToken cancellationToken)
        {
            return deletionReceipts.TryGetValue(receipt, out var completed) ? Task.FromResult(completed) : throw new AccountAccessException("Receipt is unavailable.", 404);
        }

        public Task<bool> DeleteAccountAsync(SessionRecord session, string receipt, string authorization, CancellationToken cancellationToken)
        {
            BeforeDeleteAccount?.Invoke();
            Interlocked.Increment(ref deletionCalls);
            if (AuthorizeDeletion?.Invoke(session, receipt, authorization) != true)
            {
                throw StepUpFlow.Required();
            }

            deletionReceipts[receipt] = true;
            lock (Gate)
            {
                var mine = Organizations.Values.Where(org => ScopedRole(session.UserId, org.Id).Length > 0).ToArray();
                foreach (var org in mine.Where(org => org.Members.Values.Any(member => member.UserId != session.UserId && member.Status == "active")))
                {
                    LastOwner(org, session.UserId);
                }

                foreach (var org in mine)
                {
                    org.Members.Remove(session.UserId);
                    if (!org.Members.Values.Any(member => member.Status == "active"))
                    {
                        Organizations.Remove(org.Id);
                    }
                }
                return Task.FromResult(true);
            }
        }
        // ts-auth e30ffdc2 invitation transactions emit these events; the registry is per test environment.
        private void InvitationAudit(string organizationId, string actor, string action, string target, string? detail = null)
        {
            if (!audit.TryGetValue(organizationId, out var events))
            {
                audit[organizationId] = events = [];
            }

            events.Add(new(ids.NewId(), actor, action, target, detail, DateTimeOffset.UtcNow));
        }
    }
    sealed class TestMeasurements : IHomeMeasurements
    {
        public Task<HomeMeasurement> ReadAsync(AccountContext context, string range, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HomeMeasurement { ActivityNote = "Reads and writes metered by TerraBase.", Regions = [new("GRU", "Operational", "12.00 ms", context.Project?.Databases.Count ?? 0)] });
        }
    }
    sealed class TestDatabases(TestDocuments documents) : IDatabaseBackend
    {
        public Task<ManagedDatabase> RenameAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string name, CancellationToken cancellationToken)
        {
            var current = Database(scope, databaseId);
            if (databases.Values.Any(database => database.Id != databaseId && database.ProjectId == scope.ProjectId && database.Name == name))
            {
                throw new DatabaseProviderException(HttpStatusCode.Conflict);
            }

            var renamed = current with { Name = name, PreviousName = null, ConflictOf = null };
            documents.RenameDatabase(scope, current.Name, name);
            databases[databaseId] = renamed;
            return Task.FromResult(renamed);
        }
        private readonly ConcurrentDictionary<string, ManagedDatabase> databases = new();
        private readonly TerraScale.DotNet.TerraScaleIdGenerator ids = new();
        private readonly ConcurrentDictionary<string, DatabaseIndex> indexes = new();
        private static readonly IReadOnlyList<DatabaseRegion> regions = [new("GRU", "gru", "São Paulo", "br", new("https://gru.api.terrascale.tech"), true), new("SJC", "sjc", "San Francisco Bay Area", "us", new("https://sjc.api.terrascale.tech"), true)];
        public void SeedFixtureDatabase(ManagedDatabase database)
        {
            if (string.IsNullOrEmpty(database.Id) || string.IsNullOrEmpty(database.ProjectId) ||
                !regions.Any(region => region.Code == database.HomeRegion || region.Id == database.HomeRegion) ||
                !databases.TryAdd(database.Id, database))
            {
                throw new InvalidDataException("Invalid or duplicate fixture database.");
            }
        }
        public Task<IReadOnlyList<DatabaseRegion>> RegionsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(regions);
        }

        public Task<IReadOnlyList<DatabaseRegion>> CatalogAsync(CancellationToken cancellationToken)
        {
            return RegionsAsync(cancellationToken);
        }

        public Task<IReadOnlyList<ManagedDatabase>> ListAsync(DatabaseScope scope, DatabaseRegion home, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<ManagedDatabase>>(databases.Values.Where(item => item.ProjectId == scope.ProjectId).ToArray());
        }

        public Task<ManagedDatabase> CreateAsync(DatabaseScope scope, string name, IReadOnlyList<DatabaseRegion> placement, bool allRegions, CancellationToken cancellationToken)
        { var value = new ManagedDatabase(ids.NewId(), scope.ProjectId, name, DateTimeOffset.UtcNow, HomeRegion: placement[0].Id, ReplicaRegions: allRegions ? [] : placement.Skip(1).Select(item => item.Id).ToArray(), AllRegions: allRegions); databases[value.Id] = value; return Task.FromResult(value); }
        public Task<ManagedDatabase> BranchAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string name, CancellationToken cancellationToken)
        {
            var source = Database(scope, databaseId);
            var value = new ManagedDatabase(ids.NewId(), scope.ProjectId, name, DateTimeOffset.UtcNow, databaseId, home.Id, []);
            documents.CopyDatabase(scope, source.Name, value.Name);
            foreach (var index in indexes.Where(item => item.Key.StartsWith(databaseId + "/", StringComparison.Ordinal)).Select(item => item.Value).ToArray())
            {
                indexes[value.Id + "/" + index.Name] = index;
            }

            databases[value.Id] = value;
            return Task.FromResult(value);
        }
        public Task DeleteAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
        {
            var database = Database(scope, databaseId);
            databases.TryRemove(databaseId, out _);
            documents.DropDatabase(scope, database.Name);
            foreach (var key in indexes.Keys.Where(key => key.StartsWith(databaseId + "/", StringComparison.Ordinal)))
            {
                indexes.TryRemove(key, out _);
            }

            return Task.CompletedTask;
        }
        public Task<ManagedDatabase> SetRegionsAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, IReadOnlyList<DatabaseRegion> placement, bool allRegions, CancellationToken cancellationToken) { var changed = databases[databaseId] with { ReplicaRegions = allRegions ? [] : placement.Skip(1).Select(item => item.Id).ToArray(), AllRegions = allRegions }; databases[databaseId] = changed; return Task.FromResult(changed); }
        public Task<IReadOnlyList<CollectionStats>> CollectionsAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
        {
            return Task.FromResult(documents.Statistics(scope, Database(scope, databaseId).Name));
        }

        public Task<DatabaseSqlCatalog> SqlTablesAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
        { Database(scope, databaseId); return Task.FromResult(new DatabaseSqlCatalog([], false)); }
        public Task<DatabaseSqlResult> SqlAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string statement, bool write, CancellationToken cancellationToken)
        {
            return Task.FromResult(documents.Query(scope, databases[databaseId].Name, statement));
        }

        public Task<IReadOnlyList<DatabaseIndex>> IndexesAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
        { Database(scope, databaseId); return Task.FromResult<IReadOnlyList<DatabaseIndex>>(indexes.Where(item => item.Key.StartsWith(databaseId + "/", StringComparison.Ordinal)).Select(item => item.Value).OrderBy(item => item.Name, StringComparer.Ordinal).ToArray()); }
        public Task<DatabaseIndex> CreateIndexAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string collection, IReadOnlyList<string> fields, CancellationToken cancellationToken)
        {
            Database(scope, databaseId);
            var name = collection + "-" + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\0', fields))))[..12];
            return Task.FromResult(indexes.GetOrAdd(databaseId + "/" + name, _ => new(name, collection, fields.ToArray(), "ready")));
        }
        public Task DropIndexAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string name, CancellationToken cancellationToken)
        { Database(scope, databaseId); indexes.TryRemove(databaseId + "/" + name, out _); return Task.CompletedTask; }
        private ManagedDatabase Database(DatabaseScope scope, string id)
        {
            return databases.TryGetValue(id, out var database) && database.ProjectId == scope.ProjectId ? database : throw new HttpRequestException("Test database missing.", null, HttpStatusCode.NotFound);
        }
    }

    sealed partial class TestDocuments : IDocumentBackend
    {
        private readonly Dictionary<string, Dictionary<string, TerraBaseDocument>> collections = [];
        private long offset;
        private static string Key(DatabaseScope scope, string database, string collection)
        {
            return scope.ProjectId + "/" + database + "/" + collection;
        }

        public IReadOnlyList<CollectionStats> Statistics(DatabaseScope scope, string database)
        {
            var prefix = scope.ProjectId + "/" + database + "/";
            lock (collections)
            {
                return collections.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(item => new CollectionStats(item.Key[prefix.Length..], item.Value.Count, item.Value.Values.Sum(document => (long)System.Text.Encoding.UTF8.GetByteCount(document.Document.GetRawText())))).OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
            }
        }
        public void CopyDatabase(DatabaseScope scope, string source, string target)
        {
            var prefix = scope.ProjectId + "/" + source + "/";
            lock (collections)
            {
                foreach (var (key, rows) in collections.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                {
                    collections[Key(scope, target, key[prefix.Length..])] = rows.ToDictionary(item => item.Key, item => item.Value with { Document = item.Value.Document.Clone() }, StringComparer.Ordinal);
                }
            }
        }
        public void RenameDatabase(DatabaseScope scope, string source, string target)
        {
            if (source == target)
            {
                return;
            }

            var prefix = scope.ProjectId + "/" + source + "/";
            lock (collections)
            {
                foreach (var key in collections.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                {
                    collections[Key(scope, target, key[prefix.Length..])] = collections[key];
                    collections.Remove(key);
                }
                if (logs.Remove(scope.ProjectId + "/" + source, out var log))
                {
                    logs[scope.ProjectId + "/" + target] = log;
                }

                changed.TrySetResult();
                changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        public void DropDatabase(DatabaseScope scope, string database)
        {
            var prefix = scope.ProjectId + "/" + database + "/";
            lock (collections)
            {
                foreach (var key in collections.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                {
                    collections.Remove(key);
                }

                logs.Remove(scope.ProjectId + "/" + database);
                changed.TrySetResult();
                changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        public Task<IReadOnlyList<TerraBaseCollection>> CollectionsAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, CancellationToken cancellationToken)
        {
            var prefix = scope.ProjectId + "/" + databaseName + "/";
            lock (collections)
            {
                return Task.FromResult<IReadOnlyList<TerraBaseCollection>>(collections.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).Select(key => new TerraBaseCollection(key[prefix.Length..], DateTimeOffset.UtcNow, "")).OrderBy(item => item.Name).ToArray());
            }
        }
        public Task CreateCollectionAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, CancellationToken cancellationToken)
        { lock (collections) { collections.TryAdd(Key(scope, databaseName, collection), []); } return Task.CompletedTask; }
        public Task DropCollectionAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, CancellationToken cancellationToken)
        {
            lock (collections)
            {
                if (!collections.Remove(Key(scope, databaseName, collection)))
                {
                    throw new DatabaseProviderException(HttpStatusCode.NotFound);
                }
            }

            return Task.CompletedTask;
        }
        public Task<TerraBaseDocumentPage> DocumentsAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, TerraBaseQuery query, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (query.Filter is not null || query.Project is not null)
            {
                throw new InvalidOperationException("The demo document query fixture does not implement filters or projections.");
            }

            lock (collections)
            {
                var rows = collections.GetValueOrDefault(Key(scope, databaseName, collection))?.Values.ToArray() ?? [];
                Array.Sort(rows, (left, right) =>
                {
                    foreach (var key in query.Sort ?? [])
                    {
                        if (key.Path is null)
                        {
                            throw new InvalidOperationException("The demo document query fixture does not implement vector sorting.");
                        }

                        var compared = key.Path == "$id" ? StringComparer.Ordinal.Compare(left.Id, right.Id) : CompareValue(QueryValue(left.Document, key.Path), QueryValue(right.Document, key.Path));
                        if (compared != 0)
                        {
                            return key.Order == "desc" ? -compared : compared;
                        }
                    }
                    return StringComparer.Ordinal.Compare(left.Id, right.Id);
                });
                var offset = query.Cursor is null ? 0 : int.Parse(query.Cursor, System.Globalization.CultureInfo.InvariantCulture);
                var page = rows.Skip(offset).Take(query.Limit ?? 50).ToArray();
                return Task.FromResult(new TerraBaseDocumentPage(page, offset + page.Length < rows.Length ? (offset + page.Length).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                    query.Count == true ? new(rows.Length, true) : null));
            }
        }
        private static JsonElement QueryValue(JsonElement document, string path)
        {
            if (!path.StartsWith('/'))
            {
                throw new InvalidOperationException("The demo query sort path must be a JSON Pointer or $id.");
            }

            foreach (var segment in path[1..].Split('/'))
            {
                if (document.ValueKind != JsonValueKind.Object || !document.TryGetProperty(segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal), out document))
                {
                    return default;
                }
            }

            return document;
        }
        private static int CompareValue(JsonElement left, JsonElement right)
        {
            static int Rank(JsonElement value) => value.ValueKind switch
            { JsonValueKind.Undefined => 0, JsonValueKind.Null => 1, JsonValueKind.False => 2, JsonValueKind.True => 3, JsonValueKind.Number => 4, JsonValueKind.String => 5, _ => 6 };
            var ranked = Rank(left).CompareTo(Rank(right));
            if (ranked != 0)
            {
                return ranked;
            }

            return left.ValueKind switch
            {
                JsonValueKind.Number => left.GetDecimal().CompareTo(right.GetDecimal()),
                JsonValueKind.String => StringComparer.Ordinal.Compare(left.GetString(), right.GetString()),
                JsonValueKind.Array or JsonValueKind.Object => StringComparer.Ordinal.Compare(left.GetRawText(), right.GetRawText()),
                _ => 0
            };
        }
        public Task<TerraBaseDocumentPage> ListAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, int limit, string? cursor, CancellationToken cancellationToken)
        {
            lock (collections)
            {
                var rows = collections.GetValueOrDefault(Key(scope, databaseName, collection))?.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray() ?? [];
                var offset = cursor is null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
                var page = rows.Skip(offset).Take(limit).ToArray();
                return Task.FromResult(new TerraBaseDocumentPage(page, offset + page.Length < rows.Length ? (offset + page.Length).ToString(System.Globalization.CultureInfo.InvariantCulture) : null));
            }
        }
        public Task<TerraBaseDocument?> GetAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, string id, CancellationToken cancellationToken)
        { lock (collections)
            {
                return Task.FromResult(collections.GetValueOrDefault(Key(scope, databaseName, collection))?.GetValueOrDefault(id));
            }
        }
        public Task<TerraBaseWriteResult> WriteAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, string operation, string id, JsonElement document, string version, CancellationToken cancellationToken)
        {
            lock (collections)
            {
                if (!collections.TryGetValue(Key(scope, databaseName, collection), out var items))
                {
                    throw new HttpRequestException("Test collection missing.", null, HttpStatusCode.NotFound);
                }

                if (id.Length == 0)
                {
                    id = Guid.CreateVersion7().ToString();
                }

                var previous = items.GetValueOrDefault(id);
                if (operation == "create" && previous is not null)
                {
                    throw ProviderError(typeof(TerraBaseConflictException), HttpStatusCode.Conflict, "Test document exists.");
                }

                if (version.Length != 0 && previous?.Version != version)
                {
                    throw ProviderError(typeof(TerraBasePreconditionFailedException), HttpStatusCode.PreconditionFailed, "Test document version changed.");
                }

                var nextVersion = "tbv1-" + Guid.NewGuid().ToString("N");
                if (operation == "delete")
                {
                    items.Remove(id);
                }
                else
                {
                    items[id] = new(id, nextVersion, "value", operation == "patch" ? JsonSerializer.SerializeToElement(Merge(JsonNode.Parse(previous?.Document.GetRawText() ?? "{}"), JsonNode.Parse(document.GetRawText()))) : document.Clone());
                }

                if (operation != "delete" || previous is not null)
                {
                    RecordChange(scope, databaseName, collection, id, operation == "delete" ? "delete" : "upsert");
                }

                return Task.FromResult(new TerraBaseWriteResult(id, nextVersion, ++offset, "local_durable"));
            }
        }
        public async IAsyncEnumerable<TerraBaseExportLine> ExportAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var prefix = scope.ProjectId + "/" + databaseName + "/";
            TerraBaseExportLine[] lines;
            lock (collections)
            {
                lines = collections.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(item => item.Key, StringComparer.Ordinal)
                .SelectMany(item => item.Value.Values.OrderBy(document => document.Id, StringComparer.Ordinal).Select(document => new TerraBaseExportLine(item.Key[prefix.Length..], document.Id, document.Document.Clone()))).ToArray();
            }

            foreach (var line in lines) { cancellationToken.ThrowIfCancellationRequested(); await Task.Yield(); yield return line; }
        }
        public Task<TerraBaseCollectionSchema> SchemaAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, CancellationToken cancellationToken)
        { lock (collections) { var documents = collections.GetValueOrDefault(Key(scope, databaseName, collection))?.Values.ToArray() ?? []; var fields = documents.SelectMany(document => document.Document.EnumerateObject()).GroupBy(property => property.Name).Select(field => new TerraBaseSchemaField(field.Key, field.Count(), field.GroupBy(property => property.Value.ValueKind.ToString().ToLowerInvariant()).ToDictionary(group => group.Key, group => group.Count()))).ToArray(); return Task.FromResult(new TerraBaseCollectionSchema(collection, documents.Length, documents.Length, fields)); } }
        public DatabaseSqlResult Query(DatabaseScope scope, string databaseName, string statement)
        {
            var match = Regex.Match(statement, "^SELECT \\* FROM ([a-z][a-z0-9_-]*) LIMIT ([0-9]+)$", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                throw ProviderError(typeof(TerraBaseValidationException), HttpStatusCode.BadRequest, "Demo SQL fixture supports SELECT * FROM collection LIMIT n.");
            }

            lock (collections)
            {
                // Rows of a document collection, as TerraBase's relational API returns them: an id column, then top-level fields.
                var documents = collections.GetValueOrDefault(Key(scope, databaseName, match.Groups[1].Value))?.Values.OrderBy(item => item.Id).Take(int.Parse(match.Groups[2].Value)).ToArray() ?? [];
                var names = documents.SelectMany(item => item.Document.ValueKind == JsonValueKind.Object ? item.Document.EnumerateObject().Select(field => field.Name) : []).Where(name => name != "id").Distinct().ToArray();
                var missing = JsonSerializer.SerializeToElement<string?>(null);
                var rows = documents.Select(item => (IReadOnlyList<JsonElement>)[JsonSerializer.SerializeToElement(item.Id), .. names.Select(name => item.Document.ValueKind == JsonValueKind.Object && item.Document.TryGetProperty(name, out var value) ? value.Clone() : missing)]).ToArray();
                return new([new("id", "TEXT"), .. names.Select(name => new DatabaseSqlColumn(name, null))], rows, 0, null);
            }
        }
        private static JsonNode? Merge(JsonNode? target, JsonNode? patch)
        {
            if (patch is not JsonObject changes)
            {
                return patch?.DeepClone();
            }

            var value = target as JsonObject ?? new JsonObject();
            foreach (var (key, change) in changes) { if (change is null)
                {
                    value.Remove(key);
                }
                else
                {
                    value[key] = Merge(value[key], change);
                }
            }
            return value;
        }
        private static Exception ProviderError(Type type, HttpStatusCode status, string message)
        {
            return (Exception)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, binder: null, args: [status, message, status == HttpStatusCode.PreconditionFailed ? "precondition_failed" : "name_taken", null], culture: null)!;
        }
    }

    // Protocol fixtures serve only the E2E suite. Billing still
    // runs the official Stripe SDK, including its real request serialization.
    sealed class TestHttpFactory(HttpMessageHandler transport) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new(transport, disposeHandler: false);
        }
    }
    sealed class TestStripeTransport : HttpMessageHandler
    {
        public ConcurrentQueue<(string Field, string Url)> ReturnUrls { get; } = new();
        private readonly ConcurrentDictionary<string, Checkout> checkouts = new();
        private readonly ConcurrentDictionary<string, object> subscriptions = new();
        public ConcurrentDictionary<string, string> PendingInvoiceItemIntervals { get; } = new();
        private readonly ConcurrentDictionary<string, string> customerIds = new();
        private readonly ConcurrentDictionary<string, string> customerOrganizations = new();
        private readonly ConcurrentDictionary<string, string> checkoutIds = new();
        private readonly ConcurrentDictionary<string, Invoice[]> invoices = new();
        private readonly ConcurrentDictionary<string, InvoiceItem> invoiceItems = new();
        private readonly ConcurrentDictionary<string, string> invoiceItemIds = new();
        private string origin = "http://localhost";
        private InvoiceItem[] InvoiceItems(string customer)
        {
            return invoiceItems.Values.Where(item => item.Customer == customer).ToArray();
        }

        internal InvoiceItem[] OverageItems(string organizationId)
        {
            return invoiceItems.Values.Where(item => item.Metadata.GetValueOrDefault("terrascale_org") == organizationId).ToArray();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host != "api.stripe.com")
            {
                throw new HttpRequestException("Unexpected external request in demo: " + request.RequestUri?.Host);
            }

            var path = request.RequestUri.AbsolutePath;
            var form = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            var idempotency = request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : Guid.NewGuid().ToString();
            object value;
            if (request.Method == HttpMethod.Post && path == "/v1/customers")
            {
                var customerId = customerIds.GetOrAdd(idempotency, _ => "cus_" + Guid.NewGuid().ToString("N"));
                var organizationId = form["metadata[organization_id]"].ToString();
                if (organizationId.Length == 0 || customerOrganizations.GetOrAdd(customerId, organizationId) != organizationId)
                {
                    throw new HttpRequestException("The Stripe fixture customer must retain its original organization metadata.");
                }

                value = new { id = customerId, @object = "customer", metadata = new { organization_id = organizationId } };
            }
            else if (request.Method == HttpMethod.Get && path == "/v1/customers/search")
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                const string prefix = "metadata['organization_id']:'";
                var filter = query.GetValueOrDefault("query").ToString();
                if (!filter.StartsWith(prefix, StringComparison.Ordinal) || !filter.EndsWith("'", StringComparison.Ordinal))
                {
                    throw new HttpRequestException("Customer cleanup search must bind organization metadata.");
                }

                var organization = filter[prefix.Length..^1];
                value = new
                {
                    @object = "search_result",
                    has_more = false,
                    data = customerOrganizations.Where(pair => pair.Value == organization)
                        .Select(pair => new { id = pair.Key, @object = "customer", metadata = new { organization_id = pair.Value } }).ToArray()
                };
            }
            else if (request.Method == HttpMethod.Get && path == "/v1/checkout/sessions")
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                var customer = query.GetValueOrDefault("customer").ToString();
                if (customer.Length == 0 || query.GetValueOrDefault("limit") != "100")
                {
                    throw new HttpRequestException("Checkout cleanup must list a bounded customer scope.");
                }

                value = new { @object = "list", has_more = false, data = checkouts.Values.Where(checkout => checkout.CustomerId == customer).Select(Session).ToArray() };
            }
            else if (request.Method == HttpMethod.Get && path == "/v1/subscriptions")
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                var customer = query.GetValueOrDefault("customer").ToString();
                if (customer.Length == 0 || query.GetValueOrDefault("status") != "all" || query.GetValueOrDefault("limit") != "100")
                {
                    throw new HttpRequestException("Subscription cleanup must include all statuses in a bounded customer scope.");
                }

                value = new
                {
                    @object = "list",
                    has_more = false,
                    data = subscriptions.Values.Select(item => JsonSerializer.SerializeToElement(item))
                        .Where(item => item.GetProperty("customer").GetString() == customer).ToArray()
                };
            }
            else if (request.Method == HttpMethod.Get && path.StartsWith("/v1/customers/", StringComparison.Ordinal))
            {
                var customerId = path["/v1/customers/".Length..];
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                if (!customerOrganizations.TryGetValue(customerId, out var organizationId) || query.GetValueOrDefault("expand[0]").ToString() is not ("invoice_settings.default_payment_method" or "tax_ids"))
                {
                    throw new HttpRequestException("The Stripe fixture card read must target an existing scoped customer with the official expansion.");
                }

                value = new { id = customerId, @object = "customer", metadata = new { organization_id = organizationId }, invoice_settings = new { default_payment_method = (object?)null }, tax_ids = new { @object = "list", has_more = false, data = Array.Empty<object>() } };
            }
            else if (request.Method == HttpMethod.Get && path == "/v1/prices")
            {
                value = new { @object = "list", has_more = false, data = PlanCatalog.Plans.Where(item => item.PriceCents > 0).SelectMany(item => item.CheckoutIntervals.Select(interval => Price(item, interval))).ToArray() };
            }
            else if (request.Method == HttpMethod.Get && path == "/v1/invoices")
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                if (!query.TryGetValue("customer", out var customer) || query.GetValueOrDefault("limit") != "100")
                {
                    throw new HttpRequestException("Invoice history must request a customer and its bounded page size.");
                }

                var rows = invoices.GetValueOrDefault(customer.ToString()) ?? [];
                var after = query.GetValueOrDefault("starting_after").ToString();
                var index = after.Length == 0 ? 0 : Array.FindIndex(rows, row => row.Id == after) + 1;
                if (after.Length > 0 && index == 0)
                {
                    throw new HttpRequestException("Unknown invoice cursor.");
                }

                var page = rows.Skip(index).Take(7).ToArray();
                value = new
                {
                    @object = "list",
                    has_more = index + page.Length < rows.Length,
                    url = "/v1/invoices",
                    data = page.Select(row => new
                    {
                        id = row.Id,
                        @object = "invoice",
                        customer = customer.ToString(),
                        number = row.Number,
                        status = row.Status,
                        amount_due = row.Amount,
                        amount_remaining = row.Status == "open" ? row.Amount : 0,
                        currency = row.Currency,
                        created = row.Created.ToUnixTimeSeconds(),
                        period_start = row.Created.AddDays(-10).ToUnixTimeSeconds(),
                        period_end = row.Created.AddDays(20).ToUnixTimeSeconds(),
                        hosted_invoice_url = row.HostedUrl,
                        invoice_pdf = row.PdfUrl
                    }).ToArray()
                };
            }
            else if (request.Method == HttpMethod.Get && path == "/v1/invoiceitems")
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                if (!query.TryGetValue("customer", out var customer) || query.GetValueOrDefault("limit") != "100" || !query.ContainsKey("created[gte]"))
                {
                    throw new HttpRequestException("Invoice items must be listed for one customer, a creation window and a bounded page size.");
                }

                value = new { @object = "list", has_more = false, url = "/v1/invoiceitems", data = InvoiceItems(customer.ToString()).Select(InvoiceItemJson).ToArray() };
            }
            else if (request.Method == HttpMethod.Post && path == "/v1/invoiceitems")
            {
                if (form["currency"] != "usd" || form["customer"].ToString().Length == 0 || form["subscription"].ToString().Length == 0)
                {
                    throw new HttpRequestException("Overage items must target a USD subscription invoice.");
                }

                var id = invoiceItemIds.GetOrAdd(idempotency, _ => "ii_" + Guid.NewGuid().ToString("N"));
                value = InvoiceItemJson(invoiceItems.GetOrAdd(id, _ => new(id, form["customer"].ToString(), form["subscription"].ToString(),
                    form.Where(item => item.Key.StartsWith("metadata[", StringComparison.Ordinal)).ToDictionary(item => item.Key[9..^1], item => item.Value.ToString()))
                { Quantity = long.Parse(form["quantity"]!, CultureInfo.InvariantCulture), UnitAmountDecimal = form["unit_amount_decimal"].ToString(), Description = form["description"].ToString() }));
            }
            else if (request.Method == HttpMethod.Post && path.StartsWith("/v1/invoiceitems/", StringComparison.Ordinal))
            {
                var item = invoiceItems[path["/v1/invoiceitems/".Length..]];
                if (item.Invoice is not null)
                {
                    throw new HttpRequestException("Invoiced items are final.");
                }

                item.Quantity = long.Parse(form["quantity"]!, CultureInfo.InvariantCulture); item.UnitAmountDecimal = form["unit_amount_decimal"].ToString(); item.Description = form["description"].ToString();
                value = InvoiceItemJson(item);
            }
            else if (request.Method == HttpMethod.Delete && path.StartsWith("/v1/invoiceitems/", StringComparison.Ordinal))
            {
                if (!invoiceItems.TryRemove(path["/v1/invoiceitems/".Length..], out var item) || item.Invoice is not null)
                {
                    throw new HttpRequestException("Only pending items can be deleted.");
                }

                value = new { id = item.Id, @object = "invoiceitem", deleted = true };
            }
            else if (request.Method == HttpMethod.Post && path == "/v1/checkout/sessions")
            {
                ObserveReturn("success_url", form["success_url"].ToString());
                ObserveReturn("cancel_url", form["cancel_url"].ToString());
                var id = checkoutIds.GetOrAdd(idempotency, _ => "cs_" + Guid.NewGuid().ToString("N"));
                var checkout = checkouts.GetOrAdd(id, _ => new(id, form["customer"].ToString(), form["client_reference_id"].ToString(), form["mode"].ToString(),
                    form.Where(item => item.Key.StartsWith("metadata[", StringComparison.Ordinal)).ToDictionary(item => item.Key[9..^1], item => item.Value.ToString()), form["success_url"].ToString()));
                value = Session(checkout);
            }
            else if (request.Method == HttpMethod.Get && path.StartsWith("/v1/checkout/sessions/", StringComparison.Ordinal))
            {
                value = Session(checkouts[path[("/v1/checkout/sessions/".Length)..]]);
            }
            else if (request.Method == HttpMethod.Get && path.StartsWith("/v1/subscriptions/", StringComparison.Ordinal))
            {
                value = subscriptions[path[("/v1/subscriptions/".Length)..]];
            }
            else if (request.Method == HttpMethod.Post && path.StartsWith("/v1/subscriptions/", StringComparison.Ordinal) && form["pending_invoice_item_interval[interval]"] == "month")
            {
                PendingInvoiceItemIntervals[path["/v1/subscriptions/".Length..]] = "month";
                value = subscriptions[path["/v1/subscriptions/".Length..]];
            }
            else if (request.Method == HttpMethod.Post && path == "/v1/billing_portal/sessions")
            {
                ObserveReturn("return_url", form["return_url"].ToString());
                value = new { id = "bps_demo", @object = "billing_portal.session", url = origin + "/__test/portal" };
            }
            else
            {
                throw new HttpRequestException("Unsupported Stripe test request: " + request.Method + " " + path);
            }

            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json") };
        }
        public void SetOrigin(string value)
        {
            origin = value;
        }

        private void ObserveReturn(string field, string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "dash.terrascale.tech" ||
                uri.Port != 443 || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            {
                throw new HttpRequestException("The Stripe fixture requires the canonical dashboard return origin.");
            }

            ReturnUrls.Enqueue((field, value));
        }
        public string Complete(string id)
        {
            var checkout = checkouts[id];
            checkout.Completed = true;
            if (checkout.Mode == "subscription")
            {
                checkout.SubscriptionId = "sub_" + id[3..];
                var plan = PlanCatalog.For(checkout.Metadata["plan"]);
                subscriptions[checkout.SubscriptionId] = new
                {
                    id = checkout.SubscriptionId,
                    @object = "subscription",
                    customer = checkout.CustomerId,
                    status = plan.TrialDays > 0 ? "trialing" : "active",
                    metadata = checkout.Metadata,
                    default_payment_method = "pm_demo",
                    items = new { @object = "list", data = new[] { new { id = "si_demo", @object = "subscription_item", quantity = 1, current_period_end = (plan.TrialDays > 0 ? DateTimeOffset.UtcNow.AddDays(plan.TrialDays) : checkout.Metadata["interval"] == "annual" ? DateTimeOffset.UtcNow.AddYears(1) : DateTimeOffset.UtcNow.AddMonths(1)).ToUnixTimeSeconds(), price = Price(plan, checkout.Metadata["interval"]) } } }
                };
                invoices.TryAdd(checkout.CustomerId, Enumerable.Range(0, 24).Select(index => new Invoice("in_" + checkout.CustomerId + "_" + index,
                    "INV-" + index.ToString("D2"), index % 2 == 0 ? "open" : "paid", 500 + index,
                    index == 2 ? "eur" : "usd", DateTimeOffset.UtcNow.AddDays(1 - index),
                    index == 0 ? "http://invoice.test/unsafe" : "https://invoice.test/" + index,
                    index == 0 ? "https://user:password@invoice.test/unsafe.pdf" : "https://invoice.test/" + index + ".pdf")).ToArray());
            }
            var success = new Uri(checkout.SuccessUrl.Replace("{CHECKOUT_SESSION_ID}", Uri.EscapeDataString(id), StringComparison.Ordinal));
            return success.PathAndQuery;
        }
        private object Session(Checkout checkout)
        {
            return new
            {
                id = checkout.Id,
                @object = "checkout.session",
                customer = checkout.CustomerId,
                client_reference_id = checkout.OrganizationId,
                metadata = checkout.Metadata,
                status = checkout.Completed ? "complete" : "open",
                mode = checkout.Mode,
                url = origin + "/__test/checkout?id=" + checkout.Id,
                subscription = checkout.SubscriptionId.Length > 0 ? checkout.SubscriptionId : null,
                setup_intent = checkout.Mode == "setup" && checkout.Completed ? new { id = "seti_demo", @object = "setup_intent", status = "succeeded", payment_method = "pm_demo" } : null
            };
        }

        private static object Price(BillingPlan plan, string interval)
        {
            return new
            {
                id = "price_" + plan.Key + "_" + interval,
                @object = "price",
                active = true,
                currency = "usd",
                unit_amount = plan.IntervalPriceCents(interval),
                recurring = new { interval = interval == "annual" ? "year" : "month", interval_count = 1 },
                product = new { id = "prod_" + plan.Key, @object = "product", active = true, name = "TerraScale " + plan.Name, metadata = new { plan = plan.Key } }
            };
        }

        private sealed class Checkout(string id, string customerId, string organizationId, string mode, Dictionary<string, string> metadata, string successUrl)
        {
            public string Id { get; } = id;
            public string CustomerId { get; } = customerId;
            public string OrganizationId { get; } = organizationId;
            public string Mode { get; } = mode;
            public Dictionary<string, string> Metadata { get; } = metadata;
            public string SuccessUrl { get; } = successUrl;
            public bool Completed { get; set; }
            public string SubscriptionId { get; set; } = "";
        }
        private sealed record Invoice(string Id, string Number, string Status, long Amount, string Currency, DateTimeOffset Created, string HostedUrl, string PdfUrl);
        private static object InvoiceItemJson(InvoiceItem item)
        {
            return new
            {
                id = item.Id,
                @object = "invoiceitem",
                customer = item.Customer,
                currency = "usd",
                quantity = item.Quantity,
                description = item.Description,
                invoice = item.Invoice,
                metadata = item.Metadata,
                parent = new { type = "subscription_details", subscription_details = new { subscription = item.Subscription } },
                pricing = new { type = "price_details", unit_amount_decimal = item.UnitAmountDecimal }
            };
        }

        internal sealed class InvoiceItem(string id, string customer, string subscription, Dictionary<string, string> metadata)
        {
            public string Id { get; } = id;
            public string Customer { get; } = customer;
            public string Subscription { get; } = subscription;
            public Dictionary<string, string> Metadata { get; } = metadata;
            public long Quantity { get; set; }
            public string UnitAmountDecimal { get; set; } = "";
            public string Description { get; set; } = "";
            public string? Invoice { get; set; }
        }
    }

    sealed class TestClientAddress(TestStripeTransport stripe) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
        {
            app.Use((context, handler) => { context.Connection.RemoteIpAddress ??= IPAddress.Loopback; stripe.SetOrigin(context.Request.Scheme + "://" + context.Request.Host); return handler(context); });
            next(app);
        };
        }
    }
}
