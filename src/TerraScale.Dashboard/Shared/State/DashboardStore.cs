using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace TerraScale.Dashboard.Shared.State;

/// <summary>
/// Seeded in-memory demo data powering every page. One <see cref="AccountWorld"/>
/// per session: the demo account plus every account created through
/// registration. All state forwards to the request's active world.
/// ponytail: the active world is AsyncLocal per request, but collections are
/// not user-isolated beyond that — single-node demo ceiling; per-world stores
/// already exist, swap the singleton for a factory if multi-node matters.
/// </summary>
public sealed partial class DashboardStore
{
    private const string DemoToken = "demo-session-token";

    private static readonly AsyncLocal<AccountWorld?> ActiveWorld = new();
    private static readonly AsyncLocal<bool> ExternalIdentityActive = new();
    private static readonly AsyncLocal<string?> ExternalIdentitySubject = new();
    private static readonly AsyncLocal<string?> ExternalChangeToken = new();

    private readonly AccountWorld _demoWorld = AccountWorld.Demo();

    // ponytail: token → world map; no expiry, no refresh — demo ceiling.
    private readonly ConcurrentDictionary<string, AccountWorld> _sessions = new(StringComparer.Ordinal);

    /// <summary>Registered accounts by email. Logout drops the session token
    /// but the account survives, so signing back in works (v1 parity).</summary>
    private readonly ConcurrentDictionary<string, AccountWorld> _accounts = new(StringComparer.OrdinalIgnoreCase);

    public DashboardStore() : this(new ConfigurationBuilder().Build(), new DashboardSecurityOptions(true)) { }

    public DashboardStore(IConfiguration configuration, DashboardSecurityOptions security)
    {
        _sessions[DemoToken] = _demoWorld;
        _accounts[_demoWorld.RegisteredEmail] = _demoWorld;

        // The demo process has no durable account database. Restore this test
        // fixture at startup without committing its password, full card number, or CVV.
        var seedPassword = configuration["TS_DASHBOARD_SEED_TEST_PASSWORD"];
        var productionDemo = configuration["TS_DASHBOARD_PRODUCTION_DEMO"] == "1";
        if (security.DemoMode && (productionDemo || seedPassword is { Length: >= 12 }))
        {
            var world = AccountWorld.Fresh("Filippo Teste", "filippo@teste.com");
            world.AccountPassword = productionDemo ? "" : seedPassword!;
            if (productionDemo)
                world.SeedPasswordHash = "54ec5c837815fa36c5e732dcd44b60fe:0993f1213841e38e0525b91bd328e08411c3c9cfc569a0c06c789d845909c22a";
            world.OnboardingCompleted = true;
            world.OnboardingStepsValue = [.. world.OnboardingStepsValue.Select(step => step with { State = "completed" })];
            var organizationId = AccountWorld.NewId("org");
            var projectId = AccountWorld.NewId("prj");
            world.Organizations[organizationId] = new OrganizationWorld
            {
                Id = organizationId,
                ActiveOrganizationName = "Filippo Teste",
                PlanId = "plan_starter",
                PlanKey = "starter",
                BillingStatus = "active",
                PaymentMethodValidated = true,
                BillingRenewal = DateTimeOffset.UtcNow.AddMonths(1).ToString("O"),
                DemoCardLast4 = "4242",
                DemoCardExpiry = "06/2030",
                DemoCardholderName = "Filippo Teste",
                Projects = [new(projectId, organizationId, "Personal", "GRU", DateTimeOffset.UtcNow.ToString("O"), IsDefault: true)],
                ActiveProjectId = projectId,
                TeamMembers = [new(world.CurrentUser.Id, world.CurrentUser.Name, world.CurrentUser.Email,
                    "owner", "Active", false, DateTimeOffset.UtcNow.ToString("O"), 0)],
            };
            world.ActiveOrganizationId = organizationId;
            _accounts[world.RegisteredEmail] = world;
        }
    }

    internal AccountWorld World => ActiveWorld.Value ?? _demoWorld;

    internal AccountWorld DemoWorld => _demoWorld;

    internal bool AllowPrefetch => !ExternalIdentityActive.Value;

    /// <summary>Drops a session token (logout). The demo session stays.</summary>
    public void EndSession(string? token)
    {
        if (token is not null && token != DemoToken)
        {
            _sessions.TryRemove(token, out _);
        }
    }

    /// <summary>E2E dev shim: grants the active world an owner session.</summary>
    public void PromoteSessionToOwner() => World.PromoteToOwner();

    /// <summary>Session token of the demo account (mario@terrascale.tech).</summary>
    public static string DemoSessionToken => DemoToken;

    // --- Sessions ----------------------------------------------------------

    /// <summary>Clears request-local activation before authentication runs.</summary>
    public void ResetRequestActivation()
    {
        ActiveWorld.Value = null;
        ExternalIdentityActive.Value = false;
        ExternalIdentitySubject.Value = null;
        ExternalChangeToken.Value = null;
    }

    /// <summary>Points this request at the cookie's world. Anonymous requests
    /// keep the demo world as an inert default.</summary>
    public bool TryActivate(string? token) =>
        token is not null && _sessions.TryGetValue(token, out var world) && (ActiveWorld.Value = world) == world;

    /// <summary>Loaded external activation state: the world plus the change
    /// token the state repository handed back with it.</summary>
    public sealed record ExternalActivation(AccountWorld World, string Subject, string? ChangeToken);

    /// <summary>Loads (or first materializes) the account world for a validated
    /// live IdP subject. The identity is the key; bearer tokens are never stored
    /// by the dashboard. Async half of external activation: the request-local
    /// writes happen in <see cref="ActivateExternalWorld"/> so they land in the
    /// middleware's own frame — values set inside an awaited method never flow
    /// back to the caller.</summary>
    public async Task<ExternalActivation> LoadExternalWorldAsync(string subject, string email, string displayName, DashboardStateRepository repository, CancellationToken ct)
    {
        var key = $"idp:{subject}";
        var (world, changeToken) = await repository.LoadAsync(subject, ct);
        world ??= _accounts.GetOrAdd(key, _ => AccountWorld.Fresh(displayName, email));
        _accounts[key] = world;
        return new(world, subject, changeToken);
    }

    /// <summary>Sets the request-local activation for a loaded external world.</summary>
    public void ActivateExternalWorld(ExternalActivation activation)
    {
        ActiveWorld.Value = activation.World;
        ExternalIdentityActive.Value = true;
        ExternalIdentitySubject.Value = activation.Subject;
        ExternalChangeToken.Value = activation.ChangeToken;
    }

    public async Task PersistExternalIdentityAsync(DashboardStateRepository repository, CancellationToken ct)
    {
        if (ExternalIdentitySubject.Value is not { Length: > 0 } subject)
        {
            return;
        }

        ExternalChangeToken.Value = await repository.SaveAsync(subject, World.RegisteredEmail, World, ExternalChangeToken.Value, ct);
    }

    /// <summary>Creates a session for an existing world and activates it for
    /// the current request; returns the session token.</summary>
    public string BeginSession(AccountWorld world)
    {
        var token = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
        _sessions[token] = world;
        if (world.RegisteredEmail is { Length: > 0 })
        {
            _accounts[world.RegisteredEmail] = world;
        }
        ActiveWorld.Value = world;
        return token;
    }

    public bool IsAuthenticated(HttpContext context) =>
        ExternalIdentityActive.Value || (context.Request.Cookies[DashboardAuthMiddleware.SessionCookie] is { Length: > 0 } token
        && (token == DemoToken || _sessions.ContainsKey(token)));

    /// <summary>Finds the world whose account owns the email, if any.</summary>
    public AccountWorld? FindAccount(string email) =>
        _accounts.TryGetValue(email, out var world) ? world : null;

    /// <summary>Public invitation lookup across every world's invitations.</summary>
    public Invitation? FindInvitation(string idOrToken) =>
        _accounts.Values.SelectMany(w => w.Invitations)
            .Concat(_demoWorld.Invitations)
            .FirstOrDefault(i => i.Id == idOrToken || i.Token == idOrToken);

    /// <summary>Admin user rows: the demo platform roster, or the registered
    /// accounts when an admin session belongs to a fresh world (v1's admin API
    /// lists exactly the registered users; owner flags stay false).</summary>
    public ImmutableArray<AdminUserRow> RegisteredAdminUsers =>
        [.. _accounts.Values.Where(w => !w.IsDemo)
            .Select(w => new AdminUserRow(
                w.CurrentUser.Id, w.CurrentUser.Name, w.CurrentUser.Email, Enabled: true, Owner: false,
                CreatedAt: w.CreatedAt.ToString("O")))];

    // --- Forwarded account state -------------------------------------------

    public DashboardUser CurrentUser { get => World.CurrentUser; private set => World.CurrentUser = value; }

    public string ActiveOrganizationId => World.ActiveOrganizationId;

    public string ActiveOrganizationName { get => World.ActiveOrganizationName; private set => World.ActiveOrganizationName = value; }

    public bool OnboardingCompleted => World.OnboardingCompleted;

    public bool OwnerSession => World.OwnerSession;

    /// <summary>Fresh members render the degraded monitoring states; promoted
    /// owners and the demo account see live data.</summary>
    public bool MonitoringStale => World.MonitoringStale;

    /// <summary>List error the databases page renders for fresh members.</summary>
    public string? DatabaseListError => World.DatabaseListError;

    public ImmutableArray<DashboardProject> Projects { get => World.Projects; private set => World.Projects = value; }

    public string ActiveProjectId { get => World.ActiveProjectId; private set => World.ActiveProjectId = value; }

    public ImmutableArray<DashboardDatabase> Databases { get => World.Databases; private set => World.Databases = value; }

    public ImmutableArray<ProjectApiKey> ApiKeys { get => World.ApiKeys; private set => World.ApiKeys = value; }

    public ImmutableArray<M2mApplication> M2mApplications { get => World.M2mApplications; private set => World.M2mApplications = value; }

    public ImmutableArray<TeamMember> TeamMembers { get => World.TeamMembers; private set => World.TeamMembers = value; }

    public ImmutableArray<Invitation> Invitations { get => World.Invitations; private set => World.Invitations = value; }

    public ImmutableArray<Notification> Notifications { get => World.Notifications; private set => World.Notifications = value; }

    public ImmutableArray<AuditEvent> AuditEvents { get => World.AuditEvents; private set => World.AuditEvents = value; }

    public int AuditRetentionDays { get => World.ActiveOrganization.AuditRetentionDays; set => World.ActiveOrganization.AuditRetentionDays = value; }

    public UsageCycle Usage => World.Usage;

    public ImmutableArray<(string Range, ImmutableArray<WorkloadPoint> Points)> Workloads => World.Workloads;

    public ImmutableArray<SavedQuery> SavedQueries { get => World.SavedQueries; private set => World.SavedQueries = value; }

    public ImmutableArray<Session> Sessions { get => World.Sessions; private set => World.Sessions = value; }

    public ImmutableArray<WebhookEndpoint> Webhooks { get => World.Webhooks; private set => World.Webhooks = value; }

    public ImmutableArray<DashboardUser> AllUsers { get => World.AllUsers; private set => World.AllUsers = value; }

    /// <summary>Region catalog shared by every account (placement codes).</summary>
    public ImmutableArray<RegionInfo> Regions { get; } =
    [
        new("us-east-1", "Virginia, US", "Live", 12, 2),
        new("us-west-2", "Oregon, US", "Live", 28, 1),
        new("eu-central-1", "Frankfurt, DE", "Live", 74, 1),
        new("eu-west-1", "Dublin, IE", "Degraded", 96, 0),
        new("sa-east-1", "São Paulo, BR", "Live", 118, 1),
    ];

    /// <summary>Public pricing catalog (platform-wide, not account state).</summary>
    public ImmutableArray<PricingPlan> Plans { get; private set; } =
    [
        new("plan_free", "Free", 0m, 512, 1_000_000, "Active", 14_203),
        new("plan_starter", "Starter", 5m, 5120, 1_000_000, "Active", 0),
        new("plan_scale", "Scale", 29m, 8192, 5_000_000, "Active", 3_412),
        new("plan_dedicated", "Dedicated", 199m, 131_072, 50_000_000, "Active", 187),
        new("plan_legacy_v1", "Legacy v1", 19m, 2048, 2_000_000, "Deprecated", 42),
    ];

    // --- Queries -----------------------------------------------------------

    public DashboardProject ActiveProject => Projects.FirstOrDefault(p => p.Id == ActiveProjectId)
        ?? new DashboardProject("", ActiveOrganizationId, "No project", "sa-east-1", "");

    public ImmutableArray<DashboardDatabase> ProjectDatabases(string projectId) =>
        [.. Databases.Where(d => d.ProjectId == projectId)];

    public int UnreadNotifications => Notifications.Count(n => n.Status == "Unread");

    public string Initials(string name, string email) => AccountWorld.InitialsOf(name, email);

    // --- Mutations ---------------------------------------------------------

    public void SwitchProject(string projectId)
    {
        if (Projects.Any(p => p.Id == projectId))
        {
            ActiveProjectId = projectId;
        }
    }

    public void CreateProject(string name, string region)
    {
        if (!CanProvisionResources) throw new InvalidOperationException("This workspace needs active billing before creating resources.");
        var project = new DashboardProject($"prj_{Guid.NewGuid():N}"[..20], ActiveOrganizationId, name, region, DateTimeOffset.UtcNow.ToString("O"));
        Projects = Projects.Add(project);
        if (ActiveProjectId.Length == 0) ActiveProjectId = project.Id;
    }

    public void CreateDatabase(string name)
    {
        if (!CanProvisionResources) throw new InvalidOperationException("This workspace needs active billing before creating resources.");
        var project = ActiveProject;
        Databases = Databases.Add(new($"db_{Guid.NewGuid():N}"[..20], project.Id, name, "Provisioning", project.Region, null, 0, 0, DateTimeOffset.Now.ToString("O")));
    }

    public void DeleteDatabase(string databaseId) =>
        Databases = [.. Databases.Where(d => d.Id != databaseId)];

    public void RevokeApiKey(string keyId) =>
        ApiKeys = [.. ApiKeys.Select(k => k.KeyId == keyId ? k with { RevokedAt = DateTimeOffset.Now.ToString("O") } : k)];

    public string CreateApiKey(string name, string[] scopes, string? databaseId)
    {
        var secret = $"tsk_live_{Convert.ToHexString(Guid.NewGuid().ToByteArray())[..12].ToLowerInvariant()}";
        var key = new ProjectApiKey(
            $"key_{Guid.NewGuid():N}"[..20], ActiveProjectId, name, secret[..12], secret[^4..],
            scopes, databaseId, CurrentUser.Email, DateTimeOffset.Now.ToString("O"),
            DateTimeOffset.Now.AddMonths(6).ToString("O"), null, null);
        ApiKeys = ApiKeys.Add(key);
        return secret;
    }

    /// <summary>Records a credential Terrabase already issued (live mode):
    /// the platform owns the secret; the dashboard keeps only the masked view.</summary>
    public void CreateIssuedApiKey(string keyId, string name, string[] scopes, string? databaseId)
    {
        var key = new ProjectApiKey(
            keyId, ActiveProjectId, name, keyId[..Math.Min(12, keyId.Length)], keyId[^Math.Min(4, keyId.Length)..],
            scopes, databaseId, CurrentUser.Email, DateTimeOffset.Now.ToString("O"),
            DateTimeOffset.Now.AddMonths(6).ToString("O"), null, null);
        ApiKeys = ApiKeys.Add(key);
    }

    public void MarkNotificationRead(string id) =>
        Notifications = [.. Notifications.Select(n => n.Id == id ? n with { Status = "Read" } : n)];

    public void MarkAllNotificationsRead() =>
        Notifications = [.. Notifications.Select(n => n with { Status = "Read" })];

    public void InviteMember(string email, string role) =>
        Invitations = Invitations.Add(new($"inv_{Guid.NewGuid():N}"[..20], email, role, "Pending", CurrentUser.Email, DateTimeOffset.Now.ToString("O"), $"tok_{Guid.NewGuid():N}"[..16]));

    public void RevokeInvitation(string id) =>
        Invitations = [.. Invitations.Select(i => i.Id == id ? i with { Status = "Revoked" } : i)];

    public void SaveQuery(string name, string query) =>
        SavedQueries = SavedQueries.Add(new($"sq_{Guid.NewGuid():N}"[..20], name, query, "1", DateTimeOffset.Now.ToString("O"), CurrentUser.Email));

    public void DeleteSavedQuery(string id) =>
        SavedQueries = [.. SavedQueries.Where(q => q.Id != id)];

    public void CreateWebhook(string url) =>
        Webhooks = Webhooks.Add(new($"wh_{Guid.NewGuid():N}"[..20], ActiveProjectId, url, "Active", DateTimeOffset.Now.ToString("O"), "whsec_••••1c2d"));

    public void RevokeSession(string id) =>
        Sessions = [.. Sessions.Where(s => s.Id != id)];

    internal static ImmutableArray<WorkloadPoint> SeedWorkload(int points, double baseValue, double variance)
    {
        var random = new Random(42);
        var result = new List<WorkloadPoint>(points);
        var value = baseValue;
        for (var i = 0; i < points; i++)
        {
            value = Math.Max(50, value + (random.NextDouble() - 0.48) * variance * 0.4);
            result.Add(new WorkloadPoint(i.ToString(), Math.Round(value)));
        }
        return [.. result];
    }
}
