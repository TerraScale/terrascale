using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;
using TerraScale.DotNet;

using TerraScale.Dashboard.Features.Auth;
namespace TerraScale.Dashboard.Shared.Accounts;

// ts-auth decides which organizations and projects exist and the caller's role
// in them. The dashboard stores only its own state for those IDs.
public sealed class AccountService(IDashboardState state, TimeProvider time, ITsAuthPlatform platform, IPlatformRoleGateway roles, IHttpClientFactory http, TerraBasePlanSync plans,
    IHttpContextAccessor requests, ILogger<AccountService> logger)
{
    public const string AccountsCollection = "dashboard_accounts";
    public const string IdentitiesCollection = "dashboard_account_identities";
    public const string OrganizationsCollection = "dashboard_organizations";
    private const string PersonalName = "Personal";
    private static readonly TerraScaleIdGenerator Ids = new();

    public static string AccountStorageKey(string subject) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(subject)));
    // Retained for callers whose existing aggregate keys use the subject digest.
    public static string AccountId(string subject) => AccountStorageKey(subject);
    public static string NewId() => Ids.NewId();

    public static bool IsDeletionFence(TerraBaseDocument document) => document.State == "value" &&
        document.Document.ValueKind == JsonValueKind.Object && document.Document.EnumerateObject().Count() == 1 &&
        document.Document.TryGetProperty(nameof(DeletionFence.ErasureId), out var id) && id.ValueKind == JsonValueKind.String && TerraScaleIds.IsValid(id.GetString());

    public static async Task FenceDeletionAsync(IDashboardState state, string subject, string erasureId, CancellationToken cancellationToken)
    {
        var key = AccountStorageKey(subject);
        await FenceRecordAsync(state, IdentitiesCollection, key, erasureId, document => Read<AccountIdentityBinding>(document).UserId == subject, cancellationToken);
        await FenceRecordAsync(state, AccountsCollection, key, erasureId, document => Read<AccountRecord>(document).UserId == subject, cancellationToken);
    }

    public static Task FenceOrganizationDeletionAsync(IDashboardState state, string organizationId, string erasureId, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(organizationId)) throw new IOException("Organization erasure identity is invalid.");
        return FenceRecordAsync(state, OrganizationsCollection, organizationId, erasureId, document => Read<OrganizationRecord>(document).Id == organizationId, cancellationToken);
    }

    public static async Task FenceRecordAsync(IDashboardState state, string collection, string key, string erasureId,
        Func<TerraBaseDocument, bool> owns, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(erasureId)) throw new IOException("Erasure identity is invalid.");
        for (var attempt = 0; ; attempt++)
        {
            var document = await state.GetAsync(collection, key, cancellationToken);
            if (document is not null)
            {
                if (document.Id != key) throw new IOException("Erasure scope does not match.");
                if (IsDeletionFence(document)) return;
                if (!owns(document)) throw new IOException("Erasure ownership does not match.");
            }
            try
            {
                await state.PutAsync(collection, key, JsonSerializer.SerializeToElement(new DeletionFence(erasureId)),
                    document is null ? TerraBasePrecondition.Absent : TerraBasePrecondition.Matches(document.Version), cancellationToken);
                return;
            }
            catch (TerraBasePreconditionFailedException) when (attempt < 3) { }
        }
    }

    public async Task<AccountRecord> EnsureAsync(SessionRecord session, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(session.UserId);
        var id = AccountStorageKey(session.UserId);
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var document = await state.GetAsync(AccountsCollection, id, cancellationToken);
            var now = time.GetUtcNow();
            if (document is null)
            {
                // Reserve a single public identity first. The replica that creates the
                // account also holds the claim to create its Personal organization.
                var identity = await ReserveIdentityAsync(session.UserId, null, cancellationToken);
                var created = new AccountRecord { Id = identity.AccountId, UserId = session.UserId, Name = session.Name, Email = session.Email, CreatedAt = now, PersonalClaimUntil = now.AddMinutes(1) };
                try { await state.PutAsync(AccountsCollection, id, JsonSerializer.SerializeToElement(created), TerraBasePrecondition.Absent, cancellationToken); }
                catch (TerraBasePreconditionFailedException) { continue; }
            }
            else
            {
                var account = await ReadAccountAsync(document, session.UserId, cancellationToken);
                if (account.PersonalOrganizationId.Length != 0)
                {
                    // ts-auth owns the sign-in email; a changed address replaces the stored copy at the next sign-in.
                    if (session.Email.Length == 0 || account.Email == session.Email) return account;
                    await UpdateAccountAsync(session, current => current.Email = session.Email, cancellationToken);
                    account.Email = session.Email;
                    return account;
                }
                if (account.PersonalClaimUntil > now)
                {
                    // Another replica is creating this account's Personal organization.
                    if (waited.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Workspace setup is still in progress.");
                    await Task.Delay(100, cancellationToken);
                    continue;
                }
                account.PersonalClaimUntil = now.AddMinutes(1);
                try { await state.PutAsync(AccountsCollection, id, JsonSerializer.SerializeToElement(account), TerraBasePrecondition.Matches(document.Version), cancellationToken); }
                catch (TerraBasePreconditionFailedException) { continue; }
            }
            OrganizationRecord personal;
            try { personal = await CreatePersonalAsync(session, cancellationToken); }
            catch
            {
                // Release the claim so a retry resumes now; CreatePersonalAsync adopts whatever was created.
                using var release = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await UpdateAccountAsync(session, current => { if (current.PersonalOrganizationId.Length == 0) current.PersonalClaimUntil = null; }, release.Token); }
                catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException or OperationCanceledException) { }
                throw;
            }
            await UpdateAccountAsync(session, current =>
            {
                if (current.PersonalOrganizationId.Length != 0) return;
                current.PersonalOrganizationId = personal.Id;
                current.PersonalClaimUntil = null;
                if (!current.OrganizationIds.Contains(personal.Id)) current.OrganizationIds.Add(personal.Id);
                if (current.ActiveOrganizationId.Length != 0) return;
                current.ActiveOrganizationId = personal.Id;
                current.ActiveProjectId = personal.Projects.Values.FirstOrDefault(project => project.IsDefault)?.Id ?? "";
            }, cancellationToken);
        }
    }

    // Runs only under this account's claim. A lapsed claim means a replica
    // stopped part-way: adopt what it created instead of creating a second one.
    private async Task<OrganizationRecord> CreatePersonalAsync(SessionRecord session, CancellationToken cancellationToken)
    {
        var live = (await platform.ListOrganizationsAsync(session, cancellationToken))
            .Where(org => org.Name == PersonalName && org.CreatedBy == session.UserId && org.Role == "owner")
            .OrderBy(org => org.CreatedAt).ThenBy(org => org.Id, StringComparer.Ordinal).FirstOrDefault()
            ?? await platform.CreateOrganizationAsync(session, PersonalName, cancellationToken);
        var project = await PersonalProjectAsync(session, live.Id, cancellationToken);
        var org = new OrganizationRecord
        {
            Id = live.Id,
            Name = live.Name,
            Slug = Slug(live.Name),
            Kind = "personal",
            OwnerUserId = session.UserId,
            CreatedAt = live.CreatedAt,
            Members = new() { [session.UserId] = new() { UserId = session.UserId, Name = session.Name, Email = session.Email, Role = "owner", CreatedAt = live.CreatedAt } },
            Projects = new() { [project.Id] = new() { Id = project.Id, OrganizationId = live.Id, Name = project.Name, Slug = Slug(project.Name), IsDefault = true, CreatedAt = project.CreatedAt } }
        };
        try { await state.PutAsync(OrganizationsCollection, org.Id, JsonSerializer.SerializeToElement(org), TerraBasePrecondition.Absent, cancellationToken); }
        catch (TerraBasePreconditionFailedException)
        {
            var stored = await state.GetAsync(OrganizationsCollection, org.Id, cancellationToken) ?? throw new IOException("Personal organization state did not persist.");
            org = Read<OrganizationRecord>(stored);
            // ts-auth's live list proved the caller owns it; the stored settings only confirm the Personal kind.
            if (stored.Id != live.Id || org.Id != live.Id || org.Kind != "personal")
                throw new AccountAccessException("Personal organization identity does not match.");
            org.Name = live.Name;
            org.OwnerUserId = session.UserId;
            if (org.DeletedProjectIds.Contains(project.Id)) throw new AccountAccessException("Project is unavailable.", 404);
            org.Projects.TryAdd(project.Id, new() { Id = project.Id, OrganizationId = live.Id, Name = project.Name, IsDefault = true, CreatedAt = project.CreatedAt });
        }
        return org;
    }

    private async Task<TerraScaleAuthPlatformProject> PersonalProjectAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
    {
        TerraScaleAuthPlatformProject? Existing(IEnumerable<TerraScaleAuthPlatformProject> projects) => projects.Where(project => project.Name == PersonalName)
            .OrderBy(project => project.CreatedAt).ThenBy(project => project.Id, StringComparer.Ordinal).FirstOrDefault();
        if (Existing(await platform.ListProjectsAsync(session, organizationId, cancellationToken)) is { } existing) return existing;
        try { return await platform.CreateProjectAsync(session, organizationId, PersonalName, cancellationToken); }
        catch (AccountAccessException error) when (error.Code == "name_taken")
        {
            return Existing(await platform.ListProjectsAsync(session, organizationId, cancellationToken))
                ?? throw new IOException("The Personal project is unavailable.");
        }
    }

    public async Task<AccountContext> GetContextAsync(SessionRecord session, CancellationToken cancellationToken,
        string? organizationId = null, string? projectId = null)
    {
        var loadedAt = ServerTiming.Now();
        // The account and ts-auth's organization list load together; a Personal organization this call just
        // created is listed again below, so the parallel list never hides it.
        var listing = platform.ListOrganizationsAsync(session, cancellationToken);
        var account = await EnsureAsync(session, cancellationToken);
        // The organization this view selects also needs its members and ownership transfer; they load with its projects.
        var accessFor = organizationId ?? (projectId is null ? account.ActiveOrganizationId : null);
        // Prefetch projects only: the organization list must prove an organization role before access reads start.
        var early = accessFor is { Length: > 0 } && TerraScaleIds.IsValid(accessFor)
            ? Deferred(() => platform.ListProjectsAsync(session, accessFor, cancellationToken)) : null;
        // A refusal for an organization the list then leaves out is only ever a discarded task, never this call's failure.
        static async Task<T> Deferred<T>(Func<Task<T>> read) => await read();
        var lives = await listing;
        if (account.PersonalOrganizationId.Length != 0 && lives.All(live => live.Id != account.PersonalOrganizationId))
            lives = await platform.ListOrganizationsAsync(session, cancellationToken);
        var prefetched = new System.Collections.Concurrent.ConcurrentDictionary<string, OrganizationAccess>(StringComparer.Ordinal);
        async Task HydrateSelectedAsync(OrganizationRecord org)
        {
            if (!org.MfaRequired && org.ContainsMember(session.UserId)) ApplyAccess(org, session, prefetched.TryGetValue(org.Id, out var access) ? access : await FetchAccessAsync(session, org.Id, cancellationToken));
        }
        var loaded = await Task.WhenAll(lives.Where(live => TerraScaleIds.IsValid(live.Id) && (live.Role is null or "owner" or "admin" or "member" or "viewer")).Select(async live =>
        {
            // Only a platform admin, checked against the signed access token on every request, sees the platform organization.
            var platformOrg = PlatformDatabases.IsPlatform(live.Id);
            if (platformOrg && await roles.PlatformRoleAsync(session, cancellationToken) != "admin") return null;
            var collaborator = live.Role is null;
            var access = !collaborator && live.Id == accessFor ? FetchAccessAsync(session, live.Id, cancellationToken) : null;
            var stored = OrganizationStateAsync(live, cancellationToken, create: !collaborator);
            var projects = live.Id == accessFor && early is not null ? early : platform.ListProjectsAsync(session, live.Id, cancellationToken);
            var org = await stored;
            // ts-auth refuses an organization that requires two-factor to a caller without it; the
            // organization stays listed, keeps its stored projects and is blocked when selected.
            IReadOnlyList<TerraScaleAuthPlatformProject>? listed = null;
            try { listed = await projects; }
            catch (AccountAccessException error) when (error.Code == TsAuthGateway.MfaRequiredCode) { org.MfaRequired = true; }
            Hydrate(org, live, listed, session);
            if (collaborator)
            {
                if (org.Status != "active" || org.Archived || !org.MfaRequired && org.Projects.Count == 0) return null;
                // Request-only container: no organization settings, identities, billing or audit authority escape.
                return new OrganizationRecord
                {
                    Id = org.Id,
                    Name = org.Name,
                    Slug = org.Slug,
                    CreatedAt = org.CreatedAt,
                    Kind = "",
                    PlanId = "",
                    BillingStatus = "",
                    MfaRequired = org.MfaRequired,
                    Projects = org.Projects.ToDictionary(item => item.Key, item => CollaboratorProject(item.Value))
                };
            }
            await plans.PublishAsync(org, cancellationToken);
            if (platformOrg)
            {
                try { await PlatformDatabases.HydrateAsync(org, http.CreateClient("platform"), session, cancellationToken); }
                // Listed without databases while TerraBase does not answer; never cached.
                catch (Exception error) when (error is HttpRequestException or AccountAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { }
            }
            if (!org.ContainsMember(session.UserId)) return null;
            if (access is not null)
            {
                try { prefetched[org.Id] = await access; }
                // ts-auth refuses the blocked organization's members too; nothing of it is shown.
                catch (AccountAccessException) when (org.MfaRequired) { }
            }
            return org;
        }));
        var organizations = loaded.OfType<OrganizationRecord>().ToList();
        if (organizationId is not null || projectId is not null)
        {
            var selected = organizations.SingleOrDefault(org => (organizationId is null || org.Id == organizationId) &&
                (projectId is null || org.Projects.ContainsKey(projectId)))
                ?? throw new AccountAccessException("Workspace is unavailable.", 404);
            await HydrateSelectedAsync(selected);
            var selectedProject = projectId is null ? DefaultProject(selected) : selected.Projects[projectId];
            if (selectedProject is not null && selectedProject.OrganizationId != selected.Id)
                throw new AccountAccessException("Project belongs to another organization.");
            var selectedContext = new AccountContext(session, account, organizations.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray(), selected, selectedProject);
            await TerraScale.Dashboard.Features.Notifications.AllowanceNotificationInbox.HydrateAsync(state, selectedContext, cancellationToken);
            return selectedContext with { Loaded = loadedAt };
        }
        var active = organizations.FirstOrDefault(item => item.Id == account.ActiveOrganizationId);
        if (active is not null) await HydrateSelectedAsync(active);
        var project = active?.Projects.GetValueOrDefault(account.ActiveProjectId);
        if (active is null && organizations.Count > 0)
        {
            active = organizations.OrderBy(item => item.Kind != "personal").ThenBy(item => item.Name).First();
            await HydrateSelectedAsync(active);
            project = DefaultProject(active);
            await UpdateAccountAsync(session, current =>
            {
                if (current.ActiveOrganizationId != account.ActiveOrganizationId) return;
                current.ActiveOrganizationId = active.Id;
                current.ActiveProjectId = project?.Id ?? "";
            }, cancellationToken);
            account.ActiveOrganizationId = active.Id;
            account.ActiveProjectId = project?.Id ?? "";
        }
        if (project is not null && project.OrganizationId != active!.Id) throw new AccountAccessException("Project belongs to another organization.");
        var context = new AccountContext(session, account, organizations.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray(), active, project);
        await TerraScale.Dashboard.Features.Notifications.AllowanceNotificationInbox.HydrateAsync(state, context, cancellationToken);
        return context with { Loaded = loadedAt };
    }

    // An organization created outside the dashboard starts as a business workspace that needs billing.
    private async Task<OrganizationRecord> OrganizationStateAsync(TerraScaleAuthPlatformOrganization live, CancellationToken cancellationToken, bool create = true)
    {
        var document = await state.GetAsync(OrganizationsCollection, live.Id, cancellationToken);
        if (document is null)
        {
            var created = new OrganizationRecord { Id = live.Id, Name = live.Name, Slug = Slug(live.Name), Kind = "business", BillingStatus = "billing_required", OwnerUserId = live.CreatedBy, CreatedAt = live.CreatedAt };
            if (!create) return created;
            try
            {
                await state.PutAsync(OrganizationsCollection, live.Id, JsonSerializer.SerializeToElement(created), TerraBasePrecondition.Absent, cancellationToken);
                return created;
            }
            catch (TerraBasePreconditionFailedException)
            {
                document = await state.GetAsync(OrganizationsCollection, live.Id, cancellationToken) ?? throw new IOException("Organization state did not persist.");
            }
        }
        if (IsDeletionFence(document)) throw new AccountAccessException("Organization is unavailable.", 404);
        var org = Read<OrganizationRecord>(document);
        if (document.Id != live.Id || org.Id != live.Id) throw new JsonException("Organization identity is invalid.");
        return org;
    }

    // ts-auth's name, the caller's role and the project list replace the stored projection in this view.
    private static void Hydrate(OrganizationRecord org, TerraScaleAuthPlatformOrganization live, IReadOnlyList<TerraScaleAuthPlatformProject>? projects, SessionRecord session)
    {
        org.Name = live.Name;
        org.Slug = Slug(live.Name);
        org.CreatedAt = live.CreatedAt;
        if (live.Role is not null) Member(org, session, live.Role);
        if (projects is null) { if (live.Role is null) org.Projects = new(); return; }
        var saved = org.Projects;
        org.Projects = new();
        foreach (var remote in projects.Where(project => project.OrganizationId == live.Id && TerraScaleIds.IsValid(project.Id) && !org.DeletedProjectIds.Contains(project.Id) &&
            (live.Role is not null || project.Role is "viewer" or "member" or "admin")))
        {
            var project = saved.GetValueOrDefault(remote.Id) ?? new ProjectRecord { Id = remote.Id, CreatedAt = remote.CreatedAt };
            if (live.Role is null && project.Status != "active") continue;
            project.Id = remote.Id;
            project.OrganizationId = live.Id;
            project.Name = remote.Name;
            project.Slug = Slug(remote.Name);
            project.CreatedAt = remote.CreatedAt;
            project.Role = remote.Role;
            org.Projects[remote.Id] = project;
        }
    }

    private static ProjectRecord CollaboratorProject(ProjectRecord project) => new()
    {
        Id = project.Id,
        OrganizationId = project.OrganizationId,
        Name = project.Name,
        Slug = project.Slug,
        Role = project.Role,
        CreatedAt = project.CreatedAt,
        Region = project.Region,
        BackupRegion = project.BackupRegion,
        Regions = project.Regions,
        Status = project.Status,
        IsDefault = project.IsDefault,
        Databases = project.Databases.Where(item => TerraScaleIds.IsValid(item.Key) && item.Key == item.Value.Id && item.Value.ProjectId == project.Id)
            .ToDictionary(item => item.Key, item => new DatabaseRecord
            {
                Id = item.Value.Id,
                ProjectId = project.Id,
                Name = item.Value.Name,
                PreviousName = item.Value.PreviousName,
                ConflictOf = item.Value.ConflictOf,
                Region = item.Value.Region,
                Regions = item.Value.Regions,
                Endpoint = item.Value.Endpoint,
                Status = item.Value.Status,
                AllRegions = item.Value.AllRegions,
                CreatedAt = item.Value.CreatedAt
            })
    };

    // Members, the owner and a pending ownership transfer as ts-auth holds them now. Stored
    // member entries contribute only display names; they never decide membership.
    public async Task HydrateAccessAsync(OrganizationRecord org, SessionRecord session, CancellationToken cancellationToken) =>
        ApplyAccess(org, session, await FetchAccessAsync(session, org.Id, cancellationToken));

    private sealed record OrganizationAccess(IReadOnlyList<TerraScaleAuthPlatformMember> Members, TerraScaleAuthPlatformOwnershipTransfer? Transfer);

    // The two ts-auth reads are independent, so they run together.
    private async Task<OrganizationAccess> FetchAccessAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
    {
        var members = platform.ListMembersAsync(session, organizationId, cancellationToken);
        var transfer = platform.GetOwnershipTransferAsync(session, organizationId, cancellationToken);
        return new(await members, await transfer);
    }

    private static void ApplyAccess(OrganizationRecord org, SessionRecord session, OrganizationAccess access)
    {
        var members = access.Members;
        var saved = org.Members;
        org.Members = members.ToDictionary(member => member.UserId, member => new MemberRecord
        {
            UserId = member.UserId,
            Email = member.Email,
            Role = member.Role,
            CreatedAt = member.JoinedAt,
            Name = saved.GetValueOrDefault(member.UserId)?.Name is { Length: > 0 } name ? name : member.UserId == session.UserId ? session.Name : member.Email,
            JoinedOn = saved.GetValueOrDefault(member.UserId)?.JoinedOn
        });
        var owners = members.Where(member => member.Role == "owner").Select(member => member.UserId).Order(StringComparer.Ordinal).ToArray();
        org.OwnerUserId = owners.Contains(session.UserId) ? session.UserId : owners.FirstOrDefault() ?? "";
        org.PendingOwnerUserId = access.Transfer?.ToUserId ?? "";
        org.PendingOwnerExpiresAt = access.Transfer?.ExpiresAt;
    }

    // The caller's live ts-auth role decides; a demoted owner stops being this view's owner.
    private static void Member(OrganizationRecord org, SessionRecord session, string role)
    {
        var member = org.Members.GetValueOrDefault(session.UserId) ?? new MemberRecord { UserId = session.UserId, Name = session.Name, Email = session.Email };
        member.Role = role;
        member.Status = "active";
        org.Members[session.UserId] = member;
        if (role == "owner") org.OwnerUserId = session.UserId;
        else if (org.OwnerUserId == session.UserId) org.OwnerUserId = "";
    }

    private static ProjectRecord? DefaultProject(OrganizationRecord org) =>
        org.Projects.Values.Where(item => item.IsDefault).OrderBy(item => item.Id, StringComparer.Ordinal).FirstOrDefault() ??
        org.Projects.Values.OrderBy(item => item.Id, StringComparer.Ordinal).FirstOrDefault();

    public async Task UpdateAccountAsync(SessionRecord session, Action<AccountRecord> update, CancellationToken cancellationToken)
    {
        var id = AccountStorageKey(session.UserId);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var document = await state.GetAsync(AccountsCollection, id, cancellationToken) ?? throw new AccountAccessException("Account is unavailable.", 401);
            var account = await ReadAccountAsync(document, session.UserId, cancellationToken);
            var publicId = account.Id;
            var personal = account.PersonalOrganizationId;
            update(account);
            if (account.Id != publicId || account.UserId != session.UserId || personal.Length != 0 && account.PersonalOrganizationId != personal)
                throw new AccountAccessException("Account identity cannot change.");
            try
            {
                await state.PutAsync(AccountsCollection, id, JsonSerializer.SerializeToElement(account), TerraBasePrecondition.Matches(document.Version), cancellationToken);
                return;
            }
            catch (TerraBasePreconditionFailedException) when (attempt < 3) { }
        }
    }

    public Task UpdateOrganizationAsync(AccountContext context, Action<OrganizationRecord> update, CancellationToken cancellationToken) =>
        UpdateOrganizationAsync(context, org => { update(org); return Task.CompletedTask; }, cancellationToken);

    // The update runs once per compare-and-set attempt, after the stored document and ts-auth's
    // identity are read, so ts-auth state it reads itself is never older than the document it decides on.
    public async Task UpdateOrganizationAsync(AccountContext context, Func<OrganizationRecord, Task> update, CancellationToken cancellationToken)
    {
        var id = context.Organization?.Id ?? throw new AccountAccessException("Select an organization.");
        for (var attempt = 0; attempt < 4; attempt++)
        {
            // Every attempt validates against ts-auth's current organization, projects and members; the stored
            // document contributes only dashboard settings, and its identity copies are refreshed on write.
            var live = (await platform.ListOrganizationsAsync(context.Session, cancellationToken)).FirstOrDefault(org => org.Id == id);
            if (live?.Role is not { Length: > 0 }) throw new AccountAccessException("Organization membership is required.");
            var document = await state.GetAsync(OrganizationsCollection, id, cancellationToken) ?? throw new AccountAccessException("Organization is unavailable.", 404);
            if (IsDeletionFence(document)) throw new AccountAccessException("Organization is unavailable.", 404);
            var org = Read<OrganizationRecord>(document);
            if (document.Id != id || org.Id != id) throw new JsonException("Organization identity is invalid.");
            Hydrate(org, live, await platform.ListProjectsAsync(context.Session, id, cancellationToken), context.Session);
            await HydrateAccessAsync(org, context.Session, cancellationToken);
            Member(org, context.Session, live.Role);
            if (!org.ContainsMember(context.UserId)) throw new AccountAccessException("Organization membership is required.");
            var recorded = org.AuditEvents.Count;
            await update(org);
            if (org.Projects.Keys.Any(org.DeletedProjectIds.Contains)) throw new AccountAccessException("Project is unavailable.", 404);
            for (var index = recorded; index < org.AuditEvents.Count; index++)
                if (org.AuditEvents[index].Ip is null) org.AuditEvents[index] = org.AuditEvents[index] with { Ip = ClientIp };
            if (org.Id != id) throw new AccountAccessException("Organization identity cannot change.");
            try
            {
                await state.PutAsync(OrganizationsCollection, id, JsonSerializer.SerializeToElement(org), TerraBasePrecondition.Matches(document.Version), cancellationToken);
                return;
            }
            catch (TerraBasePreconditionFailedException) when (attempt < 3) { }
        }
    }

    public DateTimeOffset Now => time.GetUtcNow();
    // The client address of this request, as the forwarded-headers middleware resolved it from the loopback edge.
    public string? ClientIp => requests.HttpContext?.Connection.RemoteIpAddress is { } ip ? (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString() : null;

    // Records audit events of a change its provider already committed: a failed record is logged and never fails that change.
    public async Task RecordAsync(AccountContext context, Action<OrganizationRecord> record, CancellationToken cancellationToken)
    {
        try { await UpdateOrganizationAsync(context, record, cancellationToken); }
        catch (Exception error) when (error is AccountAccessException or IOException or HttpRequestException or JsonException or TerraBaseException)
        { logger.LogWarning("Audit event was not recorded ({ErrorType})", error.GetType().Name); }
    }

    public async Task<AccountContext> SwitchOrganizationAsync(SessionRecord session, string id, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(session, cancellationToken, organizationId: id);
        var org = context.Organization!;
        var selected = context.Account;
        await UpdateAccountAsync(session, account =>
        {
            account.ActiveOrganizationId = org.Id;
            account.ActiveProjectId = context.Project?.Id ?? "";
            if (!account.OrganizationIds.Contains(org.Id)) account.OrganizationIds.Add(org.Id);
            selected = account;
        }, cancellationToken);
        return context with { Account = selected, Loaded = null };
    }

    public async Task<AccountContext> SwitchProjectAsync(SessionRecord session, string id, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(session, cancellationToken, projectId: id);
        var org = context.Organization!;
        var selected = context.Account;
        await UpdateAccountAsync(session, account =>
        {
            account.ActiveOrganizationId = org.Id;
            account.ActiveProjectId = id;
            if (!account.OrganizationIds.Contains(org.Id)) account.OrganizationIds.Add(org.Id);
            selected = account;
        }, cancellationToken);
        return context with { Account = selected, Loaded = null };
    }

    public async Task<OrganizationRecord> CreateBusinessAsync(SessionRecord session, string name, CancellationToken cancellationToken)
    {
        name = ValidName(name);
        var context = await GetContextAsync(session, cancellationToken);
        var duplicate = context.Organizations.FirstOrDefault(org => org.OwnerUserId == session.UserId && org.Slug == Slug(name));
        if (duplicate is not null)
        {
            if (duplicate.Kind == "business" && duplicate.BillingStatus == "billing_required")
            {
                await SwitchOrganizationAsync(session, duplicate.Id, cancellationToken);
                return duplicate;
            }
            throw new AccountAccessException("You already own an organization with this name.", 409);
        }
        var live = await platform.CreateOrganizationAsync(session, name, cancellationToken);
        var org = new OrganizationRecord
        {
            Id = live.Id,
            Name = live.Name,
            Slug = Slug(live.Name),
            Kind = "business",
            PlanId = OrganizationRecord.StarterPlanId,
            BillingStatus = "billing_required",
            OwnerUserId = session.UserId,
            CreatedAt = live.CreatedAt,
            Members = new() { [session.UserId] = new() { UserId = session.UserId, Name = session.Name, Email = session.Email, Role = "owner", CreatedAt = live.CreatedAt } }
        };
        // A context read between creation and this write stores the default business state; keep it.
        try { await state.PutAsync(OrganizationsCollection, org.Id, JsonSerializer.SerializeToElement(org), TerraBasePrecondition.Absent, cancellationToken); }
        catch (TerraBasePreconditionFailedException)
        {
            var current = await state.GetAsync(OrganizationsCollection, org.Id, cancellationToken);
            if (current is null || IsDeletionFence(current)) throw new AccountAccessException("Organization is unavailable.", 404);
        }
        await UpdateAccountAsync(session, account => { account.OrganizationIds.Add(org.Id); account.ActiveOrganizationId = org.Id; account.ActiveProjectId = ""; }, cancellationToken);
        return org;
    }

    public static string ValidName(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 128 || name.Any(char.IsControl) || !name.Any(char.IsLetterOrDigit))
            throw new AccountAccessException("Names need 1–128 characters including a letter or digit.", 422);
        return name;
    }

    public static string Slug(string name) => string.Join('-', name.ToLowerInvariant().Split(name.Where(character => !char.IsAsciiLetterOrDigit(character)).Distinct().ToArray(), StringSplitOptions.RemoveEmptyEntries));

    public static T Read<T>(TerraBaseDocument document)
    {
        if (document.State != "value") throw new IOException("Dashboard record has a causal conflict.");
        if (IsDeletionFence(document)) throw new IOException("Dashboard record was erased.");
        var value = document.Document.Deserialize<T>() ?? throw new JsonException("Dashboard record is invalid.");
        if (value is AccountRecord account && (account.OrganizationIds is null || account.Notifications is null || account.Name is null || account.Email is null || account.ActiveOrganizationId is null || account.ActiveProjectId is null || account.PersonalOrganizationId is null))
            throw new JsonException("Dashboard account structure is invalid.");
        if (value is OrganizationRecord org && (org.DeletedProjectIds is null || org.DeletedProjectIds.Any(id => !TerraScaleIds.IsValid(id)) || org.PendingProjectDeletions is null || org.Projects is null || org.Projects.Keys.Any(org.DeletedProjectIds.Contains) || org.Members is null || org.AuditEvents is null || org.Invitations is null || org.InvitationReservations is null || org.InvitationReservations.Values.Any(reservation => reservation?.Known is null) || org.Invitations.Values.Any(invite => invite is null) || org.Name is null || org.OwnerUserId is null || org.Members.Values.Any(member => member is null) || org.Projects.Values.Any(project => project is null || project.Databases is null || project.Regions is null)))
            throw new JsonException("Dashboard organization structure is invalid.");
        return value;
    }

    private async Task<AccountRecord> ReadAccountAsync(TerraBaseDocument document, string subject, CancellationToken cancellationToken)
    {
        if (IsDeletionFence(document)) throw IdentityMismatch();
        var key = AccountStorageKey(subject);
        var account = Read<AccountRecord>(document);
        if (document.Id != key || account.UserId != subject || !ValidPublicId(account.Id, key)) throw IdentityMismatch();
        var identityDocument = await state.GetAsync(IdentitiesCollection, key, cancellationToken);
        AccountIdentityBinding identity;
        if (identityDocument is not null) identity = ReadIdentity(identityDocument, subject);
        else if (account.Id == key)
            // Only validated existing SHA public IDs may bootstrap their
            // unchanged claim. Imported public ULIDs must arrive bound.
            identity = await ReserveIdentityAsync(subject, key, cancellationToken);
        else throw IdentityMismatch();
        if (identity.AccountId != account.Id) throw IdentityMismatch();
        return account;
    }

    private async Task<AccountIdentityBinding> ReserveIdentityAsync(string subject, string? validatedLegacyId, CancellationToken cancellationToken)
    {
        var key = AccountStorageKey(subject);
        var existing = await state.GetAsync(IdentitiesCollection, key, cancellationToken);
        if (existing is not null) return ReadIdentity(existing, subject);
        if (validatedLegacyId is not null && validatedLegacyId != key) throw IdentityMismatch();
        var candidate = new AccountIdentityBinding(subject, validatedLegacyId ?? NewId());
        try
        {
            await state.PutAsync(IdentitiesCollection, key, JsonSerializer.SerializeToElement(candidate), TerraBasePrecondition.Absent, cancellationToken);
            return candidate;
        }
        catch (TerraBasePreconditionFailedException)
        {
            var winner = await state.GetAsync(IdentitiesCollection, key, cancellationToken) ?? throw new IOException("Account identity reservation did not persist.");
            return ReadIdentity(winner, subject);
        }
    }

    private static AccountIdentityBinding ReadIdentity(TerraBaseDocument document, string subject)
    {
        if (IsDeletionFence(document)) throw IdentityMismatch();
        var key = AccountStorageKey(subject);
        var identity = Read<AccountIdentityBinding>(document);
        if (document.Id != key || identity.UserId != subject || !ValidPublicId(identity.AccountId, key)) throw IdentityMismatch();
        return identity;
    }

    private static bool ValidPublicId(string? id, string key) => TerraScaleIds.IsValid(id) || id == key;
    private static AccountAccessException IdentityMismatch() => new("Account identity does not match.", 401);
}
