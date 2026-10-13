using TerraScale.Dashboard.Features.Data;

namespace TerraScale.Dashboard.Features.ApiKeys;

public sealed class ApiKeyService(IDatabaseBackend backend, DatabaseService databases, IDataCredentialRegistry credentials)
{
    public async Task<IReadOnlyList<ProjectCredential>> ListAsync(AccountContext context, CancellationToken cancellationToken)
    {
        if (context.Project is null) return [];
        return (await RegionalCredentialsAsync(context, cancellationToken)).Select(item => item.Key).OrderBy(key => key.Name, StringComparer.Ordinal).ToArray();
    }

    public async Task<CreatedProjectCredential> CreateAsync(AccountContext context, string kind, string name, string databaseId, IEnumerable<string> scopes, CancellationToken cancellationToken)
    {
        if (!context.CanManage) throw new AccountAccessException("Organization management access is required.");
        name = name.Trim();
        if (name.EnumerateRunes().Count() is 0 or > 128 || name.Any(char.IsControl)) throw new AccountAccessException("Enter a name of up to 128 characters and choose a database access level.", 422);
        if (kind is not ("key" or "api-key")) throw new AccountAccessException("TerraBase data keys cannot be rotated, and machine clients are not issued yet.", 503);
        var database = databaseId.Length > 0 ? DatabaseService.Database(context, databaseId) : null;
        var permissions = scopes.Select(scope => scope.Trim()).Select(scope => scope switch { "database:read" => "documents:read", "database:write" => "documents:write", _ => scope }).Distinct(StringComparer.Ordinal).ToArray();
        if (permissions.Length == 0 || permissions.Any(scope => scope is not ("documents:read" or "documents:write"))) throw new AccountAccessException("Choose read and/or write permission.", 422);
        var home = await databases.HomeAsync(context, database, cancellationToken);
        var scope = DatabaseService.Scope(context);
        await backend.RegisterAsync(scope, home, cancellationToken);
        return await backend.CreateCredentialAsync(scope, home, name, permissions, databaseId, cancellationToken);
    }

    public async Task RevokeAsync(AccountContext context, string keyId, CancellationToken cancellationToken)
    {
        if (!context.CanManage) throw new AccountAccessException("Organization management access is required.");
        var key = (await RegionalCredentialsAsync(context, cancellationToken)).FirstOrDefault(item => item.Key.Id == keyId);
        if (key.Key is null) throw new AccountAccessException("Credential is unavailable.", 404);
        await backend.RevokeCredentialAsync(DatabaseService.Scope(context), key.Region, keyId, cancellationToken);
    }

    public static ProjectApiKeyMetadata Metadata(ProjectCredential key) => new(key.Id, key.ProjectId, key.Name, "tb_", key.LastFour, key.Scopes,
        key.DatabaseId ?? "", "", key.CreatedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture), "", "", "", true);
    public static ProjectCredentialMetadata CredentialMetadata(ProjectCredential key)
    {
        var value = Metadata(key);
        return new(value.KeyId, value.ProjectId, value.Name, value.Prefix, value.LastFour, value.Scopes, value.DatabaseId,
            value.CreatedByEmail, value.CreatedAt, value.ExpiresAt, value.LastUsedAt, value.RevokedAt, value.IsActive, "api-key");
    }

    private async Task<IReadOnlyList<(ProjectCredential Key, DatabaseRegion Region)>> RegionalCredentialsAsync(AccountContext context, CancellationToken cancellationToken)
    {
        if (context.Project is null) return [];
        var scope = DatabaseService.Scope(context);
        var codes = context.Project.Regions.Prepend(context.Project.Region).Concat(context.Project.Databases.Values.SelectMany(DatabaseService.Regions));
        var regions = await databases.ConfiguredPlacementAsync(codes, cancellationToken);
        var keys = new List<(ProjectCredential Key, DatabaseRegion Region)>();
        var privateKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var region in regions)
        {
            foreach (var key in await backend.CredentialsAsync(scope, region, cancellationToken))
            {
                if (key.ProjectId != scope.ProjectId) throw new HttpRequestException("Credential listing returned an invalid project identity.");
                keys.Add((key, region));
            }
        }
        // An enrollment may finish while the producer list is being read.
        foreach (var region in regions)
            if (await credentials.CredentialIdAsync(scope, region, cancellationToken) is { } privateId) privateKeys.Add(privateId);
        return keys.Where(item => !privateKeys.Contains(item.Key.Id)).ToArray();
    }
}

public sealed record ApiKeysPage(TerraScale.Dashboard.UI.ShellModel Shell, IReadOnlyList<ProjectCredential> Keys, string Kind = "", string Search = "", string DatabaseId = "", string Error = "", string Create = "", string Name = "", CreatedProjectCredential? Revealed = null, string Status = "", string DraftScopes = "", string DraftDatabaseId = "", string DialogKind = "")
{
    public AccountContext Context => Shell.Context;
    public IReadOnlyList<ProjectCredential> Filtered => Keys.Where(key => Status != "inactive" && (Kind.Length == 0 || Kind == "key" || Kind == "api-key") && (DatabaseId.Length == 0 || key.DatabaseId == DatabaseId) && (Search.Length == 0 || key.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || key.LastFour.Contains(Search, StringComparison.OrdinalIgnoreCase))).ToArray();
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
        return value is "database:read" or "database:write" or "database:read,database:write" ? value : "";
    }
    public string DraftDatabaseFor(string kind) => Create == kind ? Context.Project?.Databases.GetValueOrDefault(DraftDatabaseId)?.Name ?? "All project databases (current and future)" : "All project databases (current and future)";
    public string PermissionSignals(string kind) => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["_" + kind + "Access"] = AccessFor(kind), ["_" + kind + "Database"] = DraftDatabaseFor(kind) });
    public static string PermissionSummary(string kind) => "$_" + kind + "Access === 'database:read,database:write' ? 'Can read, create, update, and delete records.' : $_" + kind + "Access === 'database:read' ? 'Can read records. Cannot create, update, or delete them.' : $_" + kind + "Access === 'database:write' ? 'Can create, update, and delete records. Cannot read or query them.' : 'Choose an access level before creating this credential.'";
    public static (string Label, string Tone, string Icon) CredentialType(ProjectCredential key)
    {
        if (key.Scopes.Contains("*")) return ("Full-access key", "rose", "ShieldAlert");
        if (key.Scopes.Any(scope => scope.EndsWith(":write", StringComparison.Ordinal)))
            return key.Scopes.Any(scope => scope.EndsWith(":read", StringComparison.Ordinal)) ? ("Read-write key", "amber", "KeyRound") : ("Write-only key", "amber", "Pencil");
        return ("Read-only key", "sky", "Eye");
    }
}
