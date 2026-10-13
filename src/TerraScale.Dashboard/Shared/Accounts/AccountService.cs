using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Shared.Accounts;

public sealed class AccountService(IDashboardState state, TimeProvider time, IWorkspaceGateway workspaces)
{
    public const string AccountsCollection = "dashboard_accounts";
    public const string IdentitiesCollection = "dashboard_account_identities";
    public const string OrganizationsCollection = "dashboard_organizations";
    private readonly TerraScaleIdGenerator publicIds = new();

    public static string AccountStorageKey(string subject) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(subject)));
    // Retained for callers whose existing aggregate keys use the subject digest.
    public static string AccountId(string subject) => AccountStorageKey(subject);
    public static string NewId() => Guid.NewGuid().ToString();

    public async Task<AccountRecord> EnsureAsync(SessionRecord session, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(session.UserId);
        var id = AccountStorageKey(session.UserId);
        var existing = await state.GetAsync(AccountsCollection, id, cancellationToken);
        if (existing is not null) return await ReadAccountAsync(existing, session.UserId, cancellationToken);
        // Reserve a single public identity before provider provisioning. A
        // retry after an interrupted enrollment reuses this create-only claim.
        var identity = await ReserveIdentityAsync(session.UserId, null, cancellationToken);
        var now = time.GetUtcNow();
        var org = await workspaces.EnsurePersonalAsync(session, cancellationToken);
        var personalId = org.Id;
        // Provider IDs and create-only writes make interrupted enrollment resumable across replicas.
        try { await state.PutAsync(OrganizationsCollection, personalId, JsonSerializer.SerializeToElement(org), TerraBasePrecondition.Absent, cancellationToken); }
        catch (TerraBasePreconditionFailedException)
        {
            var stored = await state.GetAsync(OrganizationsCollection, personalId, cancellationToken) ?? throw new IOException("Personal organization creation did not persist.");
            org = Read<OrganizationRecord>(stored);
            if (stored.Id != personalId || org.Id != personalId) throw new JsonException("Personal organization identity is invalid.");
            if (org.OwnerUserId != session.UserId || !org.ContainsMember(session.UserId)) throw new AccountAccessException("Personal organization identity does not match.");
        }
        var account = new AccountRecord
        {
            Id = identity.AccountId,
            UserId = session.UserId,
            Name = session.Name,
            Email = session.Email,
            CreatedAt = now,
            OrganizationIds = [personalId],
            ActiveOrganizationId = personalId,
            ActiveProjectId = org.Projects.Values.FirstOrDefault(project => project.IsDefault)?.Id ?? ""
        };
        try { await state.PutAsync(AccountsCollection, id, JsonSerializer.SerializeToElement(account), TerraBasePrecondition.Absent, cancellationToken); }
        catch (TerraBasePreconditionFailedException)
        {
            var stored = await state.GetAsync(AccountsCollection, id, cancellationToken) ?? throw new IOException("Account creation did not persist.");
            return await ReadAccountAsync(stored, session.UserId, cancellationToken);
        }
        return account;
    }

    public async Task<AccountContext> GetContextAsync(SessionRecord session, CancellationToken cancellationToken)
    {
        var account = await EnsureAsync(session, cancellationToken);
        var organizations = new List<OrganizationRecord>();
        foreach (var id in account.OrganizationIds.Distinct())
        {
            var document = await state.GetAsync(OrganizationsCollection, id, cancellationToken);
            if (document is null) continue;
            var candidate = Read<OrganizationRecord>(document);
            if (document.Id != id || candidate.Id != id) throw new JsonException("Organization identity is invalid.");
            if (candidate.ContainsMember(session.UserId) && await workspaces.RoleAsync(session, candidate.Id, cancellationToken) is { Length: > 0 } role)
            {
                // Provider authorization is authoritative even when the dashboard projection is stale.
                candidate.Members[session.UserId].Role = role;
                organizations.Add(candidate);
            }
        }
        var org = organizations.FirstOrDefault(item => item.Id == account.ActiveOrganizationId);
        var project = org?.Projects.GetValueOrDefault(account.ActiveProjectId);
        if (org is null && organizations.Count > 0)
        {
            org = organizations.OrderBy(item => item.Kind != "personal").ThenBy(item => item.Name).First();
            project = org.Projects.Values.FirstOrDefault(item => item.IsDefault) ?? org.Projects.Values.OrderBy(item => item.Id).FirstOrDefault();
            await UpdateAccountAsync(session, current =>
            {
                if (current.ActiveOrganizationId != account.ActiveOrganizationId) return;
                current.ActiveOrganizationId = org.Id;
                current.ActiveProjectId = project?.Id ?? "";
            }, cancellationToken);
            account.ActiveOrganizationId = org.Id;
            account.ActiveProjectId = project?.Id ?? "";
        }
        if (project is not null && project.OrganizationId != org!.Id) throw new AccountAccessException("Project belongs to another organization.");
        return new(session, account, organizations.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray(), org, project);
    }

    public async Task UpdateAccountAsync(SessionRecord session, Action<AccountRecord> update, CancellationToken cancellationToken)
    {
        var id = AccountStorageKey(session.UserId);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var document = await state.GetAsync(AccountsCollection, id, cancellationToken) ?? throw new AccountAccessException("Account is unavailable.", 401);
            var account = await ReadAccountAsync(document, session.UserId, cancellationToken);
            var publicId = account.Id;
            update(account);
            if (account.Id != publicId || account.UserId != session.UserId) throw new AccountAccessException("Account identity cannot change.");
            try
            {
                await state.PutAsync(AccountsCollection, id, JsonSerializer.SerializeToElement(account), TerraBasePrecondition.Matches(document.Version), cancellationToken);
                return;
            }
            catch (TerraBasePreconditionFailedException) when (attempt < 3) { }
        }
    }

    public async Task UpdateOrganizationAsync(AccountContext context, Action<OrganizationRecord> update, CancellationToken cancellationToken)
    {
        var id = context.Organization?.Id ?? throw new AccountAccessException("Select an organization.");
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var document = await state.GetAsync(OrganizationsCollection, id, cancellationToken) ?? throw new AccountAccessException("Organization is unavailable.", 404);
            var org = Read<OrganizationRecord>(document);
            if (document.Id != id || org.Id != id) throw new JsonException("Organization identity is invalid.");
            if (!org.ContainsMember(context.UserId)) throw new AccountAccessException("Organization membership is required.");
            var role = await workspaces.RoleAsync(context.Session, id, cancellationToken);
            if (role.Length == 0) throw new AccountAccessException("Organization membership is required.");
            org.Members[context.UserId].Role = role;
            update(org);
            if (org.Id != id) throw new AccountAccessException("Organization identity cannot change.");
            try
            {
                await state.PutAsync(OrganizationsCollection, id, JsonSerializer.SerializeToElement(org), TerraBasePrecondition.Matches(document.Version), cancellationToken);
                return;
            }
            catch (TerraBasePreconditionFailedException) when (attempt < 3) { }
        }
    }

    public async Task SwitchOrganizationAsync(SessionRecord session, string id, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(session, cancellationToken);
        var org = context.Organizations.FirstOrDefault(item => item.Id == id) ?? throw new AccountAccessException("Organization is unavailable.", 404);
        await UpdateAccountAsync(session, account =>
        {
            account.ActiveOrganizationId = org.Id;
            account.ActiveProjectId = org.Projects.Values.OrderBy(project => project.Id).FirstOrDefault(project => project.IsDefault)?.Id ??
                org.Projects.Keys.Order().FirstOrDefault() ?? "";
        }, cancellationToken);
    }

    public async Task SwitchProjectAsync(SessionRecord session, string id, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(session, cancellationToken);
        var org = context.Organizations.FirstOrDefault(item => item.Projects.TryGetValue(id, out var project) && project.OrganizationId == item.Id)
            ?? throw new AccountAccessException("Project is unavailable.", 404);
        await UpdateAccountAsync(session, account => { account.ActiveOrganizationId = org.Id; account.ActiveProjectId = id; }, cancellationToken);
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
        var org = await workspaces.CreateOrganizationAsync(session, name, cancellationToken);
        org.Kind = "business";
        org.PlanId = OrganizationRecord.StarterPlanId;
        org.BillingStatus = "billing_required";
        await state.PutAsync(OrganizationsCollection, org.Id, JsonSerializer.SerializeToElement(org), TerraBasePrecondition.Absent, cancellationToken);
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
        var value = document.Document.Deserialize<T>() ?? throw new JsonException("Dashboard record is invalid.");
        if (value is AccountRecord account && (account.OrganizationIds is null || account.Notifications is null || account.Name is null || account.Email is null || account.ActiveOrganizationId is null || account.ActiveProjectId is null))
            throw new JsonException("Dashboard account structure is invalid.");
        if (value is OrganizationRecord org && (org.Members is null || org.Projects is null || org.AuditEvents is null || org.Invitations is null || org.Invitations.Values.Any(invite => invite is null) || org.Name is null || org.OwnerUserId is null || org.Members.Values.Any(member => member is null) || org.Projects.Values.Any(project => project is null || project.Databases is null || project.Regions is null)))
            throw new JsonException("Dashboard organization structure is invalid.");
        return value;
    }

    private async Task<AccountRecord> ReadAccountAsync(TerraBaseDocument document, string subject, CancellationToken cancellationToken)
    {
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
        var candidate = new AccountIdentityBinding(subject, validatedLegacyId ?? publicIds.NewId());
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
        var key = AccountStorageKey(subject);
        var identity = Read<AccountIdentityBinding>(document);
        if (document.Id != key || identity.UserId != subject || !ValidPublicId(identity.AccountId, key)) throw IdentityMismatch();
        return identity;
    }

    private static bool ValidPublicId(string? id, string key) => TerraScaleIds.IsValid(id) || id == key;
    private static AccountAccessException IdentityMismatch() => new("Account identity does not match.", 401);

}
