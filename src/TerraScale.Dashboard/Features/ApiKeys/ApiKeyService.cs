using System.Globalization;
using System.Net;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.ApiKeys;

// ts-auth owns key metadata and issuance. Secrets are returned once and never stored by the dashboard.
public sealed class ApiKeyService(IHttpClientFactory http, TimeProvider time)
{
    public async Task<IReadOnlyList<ProjectCredential>> ListAsync(AccountContext context, CancellationToken cancellationToken)
    {
        if (context.Project is null) return [];
        var scope = DatabaseService.Scope(context);
        var client = Client();
        var result = new List<ProjectCredential>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? next = null;
        var pages = 0;
        do
        {
            var page = await client.ListProjectApiKeysAsync(context.Session.AccessToken, scope.OrganizationId, scope.ProjectId, 100, next, cancellationToken);
            foreach (var key in page.Items)
            {
                if (result.Any(item => item.Id == key.Id)) throw new HttpRequestException("Credential listing returned a repeated key.");
                result.Add(Map(context, key));
            }
            next = page.Next;
            if (!string.IsNullOrEmpty(next) && (!seen.Add(next) || ++pages >= 100))
                throw new HttpRequestException("Credential pagination did not advance within its bounds.");
        } while (!string.IsNullOrEmpty(next));
        return result.OrderBy(key => key.Name, StringComparer.Ordinal).ToArray();
    }

    public async Task<CreatedProjectCredential> CreateAsync(AccountContext context, string kind, string name, string databaseId, IEnumerable<string> scopes, int expiresInDays, CancellationToken cancellationToken)
    {
        if (!context.CanManage) throw new AccountAccessException("Organization management access is required.");
        name = name.Trim();
        if (name.EnumerateRunes().Count() is 0 or > 128 || name.Any(char.IsControl)) throw new AccountAccessException("Enter a name of up to 128 characters and choose a database access level.", 422);
        if (kind is not ("key" or "api-key")) throw new AccountAccessException("Machine clients are not issued by this view.", 503);
        var database = databaseId.Length > 0 ? DatabaseService.Database(context, databaseId) : null;
        var permissions = scopes.Select(scope => scope.Trim()).Select(MintedScope).Distinct(StringComparer.Ordinal).ToArray();
        if (permissions.Length == 0 || permissions.Any(scope => scope is not ("documents:read" or "documents:write" or "databases:read" or "databases:write"))) throw new AccountAccessException("Choose read and/or write permission.", 422);
        if (!ExpiryChoices.Contains(expiresInDays)) throw new AccountAccessException("Choose no expiry, 30 days, 90 days, or 1 year.", 422);
        // Whole seconds, so the expiry ts-auth echoes back compares exactly.
        DateTimeOffset? expiresAt = expiresInDays == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(time.GetUtcNow().ToUnixTimeSeconds()).AddDays(expiresInDays);
        var scope = DatabaseService.Scope(context);
        var result = await Client().CreateProjectApiKeyAsync(context.Session.AccessToken, scope.OrganizationId, scope.ProjectId,
            new(name, permissions, database?.Name, expiresAt), cancellationToken);
        var key = Map(context, result.ApiKey);
        if (key.Name != name || key.DatabaseName != database?.Name || key.ExpiresAt != expiresAt || !key.Scopes.Order(StringComparer.Ordinal).SequenceEqual(permissions.Order(StringComparer.Ordinal)))
            throw new HttpRequestException("Credential issuance returned different restrictions.");
        return new(key, result.Key);
    }

    public async Task RevokeAsync(AccountContext context, string keyId, CancellationToken cancellationToken)
    {
        if (!context.CanManage) throw new AccountAccessException("Organization management access is required.");
        var scope = DatabaseService.Scope(context);
        var client = Client();
        var key = await client.GetProjectApiKeyAsync(context.Session.AccessToken, scope.OrganizationId, scope.ProjectId, keyId, cancellationToken);
        if (Map(context, key).Id != keyId) throw new HttpRequestException("Credential lookup returned a different key.");
        try { await client.RevokeProjectApiKeyAsync(context.Session.AccessToken, scope.OrganizationId, scope.ProjectId, keyId, cancellationToken); }
        catch (HttpRequestException)
        {
            // A failed answer may hide a committed delete: report the key's real state, not the transport error.
            try { await client.GetProjectApiKeyAsync(context.Session.AccessToken, scope.OrganizationId, scope.ProjectId, keyId, cancellationToken); }
            catch (HttpRequestException missing) when (missing.StatusCode == HttpStatusCode.NotFound) { return; }
            throw;
        }
    }

    // After ts-auth committed a create or revoke: the dashboard records who made it and from where.
    public Task RecordAsync(AccountService accounts, AccountContext context, string keyId, bool created, CancellationToken cancellationToken) =>
        accounts.RecordAsync(context, org => org.AuditEvents.Add(new(AccountService.NewId(), context.Account.Email, created ? "api_key.created" : "api_key.revoked", keyId, "api_key", time.GetUtcNow())), cancellationToken);

    // ts-auth keeps the old secret valid for previousSecretTtl seconds (at most one day) and returns the new secret once.
    public async Task<CreatedProjectCredential> RotateAsync(AccountContext context, string keyId, int previousSecretTtl, CancellationToken cancellationToken)
    {
        if (!context.CanManage) throw new AccountAccessException("Organization management access is required.");
        if (previousSecretTtl is < 0 or > MaxPreviousSecretTtl) throw new AccountAccessException("The old secret's grace period must be between 0 and 86400 seconds.", 422);
        var scope = DatabaseService.Scope(context);
        var client = Client();
        var current = Map(context, await client.GetProjectApiKeyAsync(context.Session.AccessToken, scope.OrganizationId, scope.ProjectId, keyId, cancellationToken));
        if (current.Id != keyId) throw new HttpRequestException("Credential lookup returned a different key.");
        TerraScaleAuthPlatformApiKeySecret result;
        try { result = await client.RotateProjectApiKeyAsync(context.Session.AccessToken, scope.OrganizationId, scope.ProjectId, keyId, new(previousSecretTtl), cancellationToken); }
        catch (TerraScaleAuthException error) when (error.ErrorCode == "rotation_pending")
        {
            throw new AccountAccessException("This key's previous secret is still in its grace period. Rotate again after it expires.", 409);
        }
        var key = Map(context, result.ApiKey);
        if (key.Id != keyId || key.Name != current.Name || key.DatabaseName != current.DatabaseName || !key.Scopes.Order(StringComparer.Ordinal).SequenceEqual(current.Scopes.Order(StringComparer.Ordinal)))
            throw new HttpRequestException("Credential rotation returned different restrictions.");
        return new(key, result.Key);
    }

    public const int MaxPreviousSecretTtl = 86400;
    // Key lifetimes offered on create, in days; 0 means the key works until it is revoked.
    public static readonly IReadOnlyList<int> ExpiryChoices = [0, 30, 90, 365];

    // The form's database:* choices are aliases of the documents:* scopes ts-auth mints.
    public static string MintedScope(string scope) => scope switch { "database:read" => "documents:read", "database:write" => "documents:write", _ => scope };

    public static ProjectApiKeyMetadata Metadata(ProjectCredential key) => new(key.Id, key.ProjectId, key.Name, key.Id + "_", "", key.Scopes,
        key.DatabaseId ?? "", "", Format(key.CreatedAt), key.ExpiresAt is { } expires ? Format(expires) : "", key.LastUsedAt is { } used ? Format(used) : "", "", key.IsActive)
    { DatabaseName = key.DatabaseName };
    public static ProjectCredentialMetadata CredentialMetadata(ProjectCredential key)
    {
        var value = Metadata(key);
        return new(value.KeyId, value.ProjectId, value.Name, value.Prefix, value.LastFour, value.Scopes, value.DatabaseId,
            value.CreatedByEmail, value.CreatedAt, value.ExpiresAt, value.LastUsedAt, value.RevokedAt, value.IsActive, "api-key")
        { DatabaseName = value.DatabaseName };
    }

    private TerraScaleAuthPlatformClient Client() => new TerraScaleAuthClient(http.CreateClient("platform"),
        new TerraScaleAuthClientOptions { BaseAddress = new("https://auth.terrascale.tech/"), InstanceSlug = "terrascale" }).Platform;

    private ProjectCredential Map(AccountContext context, TerraScaleAuthPlatformApiKey key)
    {
        var scope = DatabaseService.Scope(context);
        if (!TerraScaleIds.IsValid(key.Id) || key.OrganizationId != scope.OrganizationId || key.ProjectId != scope.ProjectId ||
            string.IsNullOrWhiteSpace(key.Name) || key.Name.Any(char.IsControl) || key.Scopes.Count == 0 ||
            key.Scopes.Any(string.IsNullOrWhiteSpace) || key.Scopes.Distinct(StringComparer.Ordinal).Count() != key.Scopes.Count)
            throw new HttpRequestException("Credential returned invalid metadata or scope identity.");
        var database = context.Project!.Databases.Values.SingleOrDefault(item => item.Name == key.Database);
        return new(key.Id, scope.ProjectId, key.Name, key.Scopes, database?.Id, "", key.CreatedAt)
        { DatabaseName = key.Database, ExpiresAt = key.ExpiresAt, LastUsedAt = key.LastUsedAt, IsActive = key.ExpiresAt is null || key.ExpiresAt > time.GetUtcNow() };
    }
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

public sealed record ApiKeysPage(TerraScale.Dashboard.Shared.UI.ShellModel Shell, IReadOnlyList<ProjectCredential> Keys, string Kind = "", string Search = "", string DatabaseId = "", string Error = "", string Create = "", string Name = "", CreatedProjectCredential? Revealed = null, string Status = "", string DraftScopes = "", string DraftDatabaseId = "", string DialogKind = "", string DraftExpiresInDays = "")
{
    public AccountContext Context => Shell.Context;
    public IReadOnlyList<ProjectCredential> Filtered => Keys.Where(key => (Status.Length == 0 || (Status == "active") == key.IsActive) && (Kind.Length == 0 || Kind == "key" || Kind == "api-key") && (DatabaseId.Length == 0 || key.DatabaseId == DatabaseId) && (Search.Length == 0 || key.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || key.Id.Contains(Search, StringComparison.OrdinalIgnoreCase))).ToArray();
    public string TabKind => Kind is "key" or "api-key" ? "key" : Kind == "m2m" ? "m2m" : "";
    public string TabLabel => TabKind switch { "key" => "API keys", "m2m" => "M2M clients", _ => "All credentials" };
    public string SearchLabel => TabKind switch { "key" => "Search API keys", "m2m" => "Search M2M clients", _ => "Search credentials" };
    public string DatabaseFilterTitle => Context.Project?.Databases.GetValueOrDefault(DatabaseId)?.Name ?? "All databases";
    public string With(string path, string? kind = null, bool clear = false, string? create = null)
    {
        var values = new SortedDictionary<string, string?> { ["kind"] = kind ?? TabKind };
        if (!clear) { values["databaseId"] = DatabaseId; values["q"] = Search; values["status"] = Status; }
        if (create is not null) values["create"] = create;
        var query = string.Join('&', values.Where(value => !string.IsNullOrEmpty(value.Value)).Select(value => System.Net.WebUtility.UrlEncode(value.Key) + "=" + System.Net.WebUtility.UrlEncode(value.Value)));
        return query.Length == 0 ? path : path + "?" + query;
    }
    public string AccessFor(string kind)
    {
        if (Create != kind || DraftScopes.Length == 0 && Error.Length == 0) return "database:read";
        var scopes = DraftScopes.Split([',', ' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(scope => scope.Replace("documents:", "database:", StringComparison.Ordinal)).Distinct().Order(StringComparer.Ordinal).ToArray();
        var value = string.Join(',', scopes);
        return value is "database:read" or "database:write" or "database:read,database:write" or Full ? value : "";
    }
    public string DraftDatabaseFor(string kind) => Create == kind ? Context.Project?.Databases.GetValueOrDefault(DraftDatabaseId)?.Name ?? "All project databases (current and future)" : "All project databases (current and future)";
    public string PermissionSignals(string kind) => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["_" + kind + "Access"] = AccessFor(kind), ["_" + kind + "Database"] = DraftDatabaseFor(kind) });
    // Documents plus database management: create and delete databases, indexes and saved queries (a seed or migration script).
    public const string Full = "database:read,database:write,databases:read,databases:write";
    public static string Summary(string access) => access switch
    {
        Full => "Can read and change records, and create and delete databases and indexes.",
        "database:read,database:write" => "Can read, create, update, and delete records.",
        "database:read" => "Can read records. Cannot create, update, or delete them.",
        "database:write" => "Can create, update, and delete records. Cannot read or query them.",
        _ => "Choose an access level before creating this credential.",
    };
    public static string PermissionSummary(string kind) => "$_" + kind + "Access === '" + Full + "' ? '" + Summary(Full) + "' : $_" + kind + "Access === 'database:read,database:write' ? 'Can read, create, update, and delete records.' : $_" + kind + "Access === 'database:read' ? 'Can read records. Cannot create, update, or delete them.' : $_" + kind + "Access === 'database:write' ? 'Can create, update, and delete records. Cannot read or query them.' : 'Choose an access level before creating this credential.'";
    public static (string Label, string Tone, string Icon) CredentialType(ProjectCredential key)
    {
        if (key.Scopes.Contains("*")) return ("Full-access key", "rose", "ShieldAlert");
        if (key.Scopes.Any(scope => scope.EndsWith(":write", StringComparison.Ordinal)))
            return key.Scopes.Any(scope => scope.EndsWith(":read", StringComparison.Ordinal)) ? ("Read-write key", "amber", "KeyRound") : ("Write-only key", "amber", "Pencil");
        return ("Read-only key", "sky", "Eye");
    }
}
