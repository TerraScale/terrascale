using System.Globalization;
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

    public async Task<CreatedProjectCredential> CreateAsync(AccountContext context, string kind, string name, string databaseId, IEnumerable<string> scopes, CancellationToken cancellationToken)
    {
        if (!context.CanManage) throw new AccountAccessException("Organization management access is required.");
        name = name.Trim();
        if (name.EnumerateRunes().Count() is 0 or > 128 || name.Any(char.IsControl)) throw new AccountAccessException("Enter a name of up to 128 characters and choose a database access level.", 422);
        if (kind is not ("key" or "api-key")) throw new AccountAccessException("Machine clients are not issued by this view.", 503);
        var database = databaseId.Length > 0 ? DatabaseService.Database(context, databaseId) : null;
        var permissions = scopes.Select(scope => scope.Trim()).Select(scope => scope switch { "database:read" => "documents:read", "database:write" => "documents:write", _ => scope }).Distinct(StringComparer.Ordinal).ToArray();
        if (permissions.Length == 0 || permissions.Any(scope => scope is not ("documents:read" or "documents:write" or "databases:read" or "databases:write"))) throw new AccountAccessException("Choose read and/or write permission.", 422);
        var scope = DatabaseService.Scope(context);
        var result = await Client().CreateProjectApiKeyAsync(context.Session.AccessToken, scope.OrganizationId, scope.ProjectId,
            new(name, permissions, database?.Name), cancellationToken);
        var key = Map(context, result.ApiKey);
        if (key.Name != name || key.DatabaseName != database?.Name || !key.Scopes.Order(StringComparer.Ordinal).SequenceEqual(permissions.Order(StringComparer.Ordinal)))
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
        await client.RevokeProjectApiKeyAsync(context.Session.AccessToken, scope.OrganizationId, scope.ProjectId, keyId, cancellationToken);
    }

    public static ProjectApiKeyMetadata Metadata(ProjectCredential key) => new(key.Id, key.ProjectId, key.Name, key.Id + "_", "", key.Scopes,
        key.DatabaseId ?? "", "", Format(key.CreatedAt), key.ExpiresAt is { } expires ? Format(expires) : "", "", "", key.IsActive)
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
        { DatabaseName = key.Database, ExpiresAt = key.ExpiresAt, IsActive = key.ExpiresAt is null || key.ExpiresAt > time.GetUtcNow() };
    }
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

public sealed record ApiKeysPage(TerraScale.Dashboard.Shared.UI.ShellModel Shell, IReadOnlyList<ProjectCredential> Keys, string Kind = "", string Search = "", string DatabaseId = "", string Error = "", string Create = "", string Name = "", CreatedProjectCredential? Revealed = null, string Status = "", string DraftScopes = "", string DraftDatabaseId = "", string DialogKind = "")
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
