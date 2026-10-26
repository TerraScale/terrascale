using System.Text.RegularExpressions;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.NotFound;
using TerraScale.Dashboard.Shared.UI;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.AuthenticationManagement;

// The project's end-user tenant at ts-auth, managed with the signed-in user's own token.
// ts-auth checks the project role on every call; the dashboard also requires an organization admin for changes.
public static partial class AuthenticationConsole
{
    public const string Unreachable = "ts-auth is unreachable right now. Try again shortly.";

    public static IEndpointRouteBuilder MapAuthenticationManagement(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/auth-management/users/export", ExportUsersAsync);
        foreach (var prefix in new[] { "/auth", "/authentication" })
        {
            endpoints.MapGet(prefix, async (HttpContext http, DashboardSessions sessions, AccountService accounts, ITsAuthProjects projects) =>
            {
                var section = http.Request.Query["section"].ToString();
                return AuthenticationSection.Find(section) is not null
                    ? HttpRedirects.SeeOther(prefix + "/" + Uri.EscapeDataString(section))
                    : await PageAsync(http, sessions, accounts, projects, "overview");
            });
            endpoints.MapGet(prefix + "/{section}", async (HttpContext http, string section, DashboardSessions sessions, AccountService accounts, ITsAuthProjects projects) =>
                await PageAsync(http, sessions, accounts, projects, section));
        }
        endpoints.MapPost("/api/v1/auth-management/{**path}", async (HttpContext http, string path, DashboardSessions sessions, AccountService accounts, ITsAuthProjects projects) =>
        {
            var parts = path.Split('/');
            var key = parts[0] switch { "settings" => "sign-in", "providers" => "social", var first => first };
            var known = parts switch
            {
                ["settings" or "branding" or "configuration" or "clients"] or ["danger", "users" or "sessions" or "clients"] => true,
                ["providers", var provider] => AuthenticationProvider.Find(provider) is not null,
                ["providers", var provider, "delete"] => AuthenticationProvider.Find(provider) is not null,
                ["users", _, "disable" or "enable" or "delete"] or ["sessions", _, "revoke"] or ["clients", _, "delete"] => true,
                _ => false
            };
            if (!known) return Results.NotFound();
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return Results.Unauthorized();
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Csrf.Validate(http, form)) return Results.StatusCode(403);
            if (!context.CanManage || context.Project is null) return Results.StatusCode(403);
            var (session, project, cancel) = (context.Session, context.Project.Id, http.RequestAborted);
            try
            {
                switch (parts)
                {
                    case ["settings"]:
                        await projects.UpdateSettingsAsync(session, project, new(Methods(form["passcode"] == "on", form["password"] == "on", form["sign_up"] == "on")), cancel);
                        break;
                    case ["branding"]:
                        await projects.UpdateSettingsAsync(session, project, new(Branding: HostedBranding(form)), cancel);
                        break;
                    case ["configuration"]:
                        await projects.UpdateSettingsAsync(session, project, new(Configuration: Configuration(form["redirect_urls"], form["session_lifetime_seconds"])), cancel);
                        break;
                    case ["danger", var family]:
                        if (form["confirmation"] != context.Project.Name) throw new AccountAccessException("Type the project name exactly to confirm.", 422);
                        var record = form["record_id"].ToString();
                        if (record.Length == 0) throw new AccountAccessException("Choose a record to remove.", 422);
                        if (family == "users") await projects.DeleteUserAsync(session, project, record, cancel);
                        else if (family == "sessions") await projects.RevokeSessionAsync(session, project, record, cancel);
                        else await projects.DeleteClientAsync(session, project, record, cancel);
                        break;
                    case ["providers", var provider]:
                        await projects.SetProviderAsync(session, project, provider, Provider(provider, form["client_id"], form["client_secret"], form["issuer"], form["team_id"], form["key_id"]), cancel);
                        break;
                    case ["providers", var provider, _]: await projects.DeleteProviderAsync(session, project, provider, cancel); break;
                    case ["clients"]:
                        var created = await projects.CreateClientAsync(session, project, NewClient(form["name"], form["type"], form["redirect_uris"].ToString().Split('\n')), cancel);
                        return await PageAsync(http, context, projects, "clients", created: created);
                    case ["clients", var id, _]: await projects.DeleteClientAsync(session, project, id, cancel); break;
                    case ["sessions", var id, _]: await projects.RevokeSessionAsync(session, project, id, cancel); break;
                    case ["users", var id, "delete"]: await projects.DeleteUserAsync(session, project, id, cancel); break;
                    case ["users", var id, var action]: await projects.SetUserDisabledAsync(session, project, id, action == "disable", cancel); break;
                }
            }
            catch (Exception error) when (error is AccountAccessException or HttpRequestException or IOException)
            {
                return await PageAsync(http, context, projects, key, error is AccountAccessException access ? access.Message : Unreachable, error is AccountAccessException { StatusCode: var code } ? code : 503);
            }
            return HttpRedirects.SeeOther("/auth/" + key + "?saved=1");
        });
        return endpoints;
    }

    // At least one first factor stays on; ts-auth enforces the same rule.
    public static TerraScaleAuthProjectMethods Methods(bool passcode, bool password, bool signUp) =>
        passcode || password ? new(passcode, password, signUp) : throw new AccountAccessException("Keep email passcode or password sign-in on.", 422);

    // Empty values clear a branding field.
    public static TerraScaleAuthProjectBranding Branding(string? name, string? logoUrl, string? themeColor)
    {
        string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var (title, logo, color) = (Clean(name), Clean(logoUrl), Clean(themeColor)?.ToLowerInvariant());
        if (title is { Length: > 64 } || title?.Any(char.IsControl) == true) throw new AccountAccessException("Use a name of up to 64 characters.", 422);
        if (logo is not null && !HttpsUrl(logo))
            throw new AccountAccessException("The logo must be an https URL.", 422);
        if (color is not null && !Color().IsMatch(color)) throw new AccountAccessException("The theme color must look like #2158e8.", 422);
        return new(title, logo, color);
    }

    // A blank secret keeps the stored one; ts-auth requires it the first time. Never log the update.
    public static TerraScaleAuthProjectProviderUpdate Provider(string provider, string? clientId, string? clientSecret, string? issuer, string? teamId, string? keyId)
    {
        string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var (id, secret, url, team, key) = (Clean(clientId), Clean(clientSecret), Clean(issuer), Clean(teamId)?.ToUpperInvariant(), Clean(keyId)?.ToUpperInvariant());
        if (AuthenticationProvider.Find(provider) is not { } known) throw new AccountAccessException("Choose google, github, apple or oidc.", 422);
        if (id is null or { Length: > 256 }) throw new AccountAccessException($"Enter the {known.ClientIdLabel.ToLowerInvariant()} of up to 256 characters.", 422);
        if (secret is { Length: > 8192 }) throw new AccountAccessException("The client secret is too long.", 422);
        if (provider == "apple" && !(AppleId().IsMatch(team ?? "") && AppleId().IsMatch(key ?? "")))
            throw new AccountAccessException("The Apple team ID and key ID are 10 letters or digits.", 422);
        if (provider == "oidc" && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Query.Length == 0 && uri.Fragment.Length == 0))
            throw new AccountAccessException("The issuer must be an https URL without a query.", 422);
        return provider switch
        {
            "apple" => new(id, secret, TeamId: team, KeyId: key),
            "oidc" => new(id, secret, Issuer: url),
            _ => new(id, secret)
        };
    }

    [GeneratedRegex("^[A-Z0-9]{10}$")]
    private static partial Regex AppleId();

    public static TerraScaleAuthProjectCreateOAuthClient NewClient(string? name, string? type, IEnumerable<string> redirectUris)
    {
        var uris = redirectUris.Select(uri => uri.Trim()).Where(uri => uri.Length > 0).ToArray();
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 64) throw new AccountAccessException("Name the application in up to 64 characters.", 422);
        if (type is not ("public" or "confidential")) throw new AccountAccessException("Choose a public or confidential application.", 422);
        if (uris.Length is < 1 or > 10) throw new AccountAccessException("Add 1 to 10 redirect URIs, one per line.", 422);
        return new(name.Trim(), type, uris);
    }

    [GeneratedRegex("^#[0-9a-f]{6}$")]
    private static partial Regex Color();

    private static async Task<IResult> PageAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, ITsAuthProjects projects, string key)
    {
        if (AuthenticationSection.Find(key) is null) return await NotFoundEndpoints.PageAsync(http, sessions, accounts);
        var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
        if (context is null) return AccountEndpoints.LoginRedirect(http);
        if (context.Project is null) return HttpRedirects.SeeOther("/");
        return await PageAsync(http, context, projects, key);
    }

    private static string? Value(string query) => query.Length == 0 ? null : query;

    private static async Task<IResult> PageAsync(HttpContext http, AccountContext context, ITsAuthProjects projects, string key, string error = "", int status = 200,
        TerraScaleAuthProjectOAuthClient? created = null)
    {
        var section = AuthenticationSection.Find(key)!;
        if (key == "branding") http.Items["auth-branding"] = true;
        var query = http.Request.Query;
        var page = new AuthenticationPage(ShellModel.For(http, context, key == "overview" ? "Authentication" : "Authentication · " + section.Title), section,
            query["q"].ToString(), query["after"].ToString(), query["user"].ToString(), error, created)
        { Draft = status >= 400 && http.Request.HasFormContentType ? await http.Request.ReadFormAsync(http.RequestAborted) : null, Saved = query["saved"] == "1" };
        var (session, project, cancel) = (context.Session, context.Project!.Id, http.RequestAborted);
        try
        {
            switch (key)
            {
                case "danger":
                    page = page with
                    {
                        Users = (await projects.ListUsersAsync(session, project, null, Value(page.After), cancel)).Items,
                        Sessions = (await projects.ListSessionsAsync(session, project, null, Value(page.After), cancel)).Items,
                        Clients = await projects.ListClientsAsync(session, project, cancel)
                    };
                    break;
                case "users": var users = await projects.ListUsersAsync(session, project, Value(page.Search), Value(page.After), cancel); page = page with { Users = users.Items, Next = users.Next }; break;
                case "sessions": var live = await projects.ListSessionsAsync(session, project, Value(page.User), Value(page.After), cancel); page = page with { Sessions = live.Items, Next = live.Next }; break;
                case "clients": page = page with { Clients = await projects.ListClientsAsync(session, project, cancel) }; break;
                default: page = page with { Settings = await projects.GetSettingsAsync(session, project, cancel) }; break;
            }
        }
        catch (Exception failure) when (failure is AccountAccessException or HttpRequestException or IOException)
        {
            if (error.Length == 0) page = page with { Error = failure is AccountAccessException ? failure.Message : Unreachable };
        }
        var result = AuthenticationView.Create(page);
        result.StatusCode = status;
        return result;
    }
}

public sealed record AuthenticationPage(ShellModel Shell, AuthenticationSection Section, string Search, string After, string User, string Error,
    TerraScaleAuthProjectOAuthClient? Created)
{
    public AccountContext Context => Shell.Context;
    public TerraScaleAuthProjectSettings? Settings { get; init; }
    public IReadOnlyList<TerraScaleAuthProjectUser>? Users { get; init; }
    public IReadOnlyList<TerraScaleAuthProjectSession>? Sessions { get; init; }
    public IReadOnlyList<TerraScaleAuthProjectOAuthClient>? Clients { get; init; }
    public string? Next { get; init; }
    public IFormCollection? Draft { get; init; }
    public bool Saved { get; init; }
    public string Heading => Section.Key == "overview" ? "Authentication" : "Authentication: " + Section.Title;
}

public sealed record AuthenticationSection(string Key, string Title, string Description, string Icon, string Group)
{
    public static AuthenticationSection? Find(string key) => All.FirstOrDefault(section => section.Key == key);
    public static readonly AuthenticationSection[] All =
    [
        new("overview", "Overview", "Issuer, hosted sign-in and totals for this project's end users.", "LayoutDashboard", ""),
        new("users", "Users", "People who signed up to your application. Disabling a user ends every session.", "Users", "Users"),
        new("sessions", "Sessions", "Live end-user sessions. Revoking one signs that sign-in out.", "Monitor", "Users"),
        new("sign-in", "Sign-in methods", "How end users sign in on the hosted pages, and whether they can sign up.", "LockKeyhole", "Sign-in"),
        new("social", "Social sign-in", "Continue with Google, GitHub, Apple or another OpenID Connect provider on the hosted pages.", "Fingerprint", "Sign-in"),
        new("configuration", "Configuration", "Exact redirect policy and lifetime of end-user sessions.", "Settings", "Sign-in"),
        new("clients", "OAuth applications", "Applications that sign users in through the authorization code flow with PKCE.", "Boxes", "Developers"),
        new("branding", "Branding", "Brand your hosted pages and verification, sign-in and password reset emails.", "Wand2", "Customize"),
        new("danger", "Danger zone", "Revoke sessions and permanently remove end users or OAuth applications.", "TriangleAlert", "Danger")
    ];
}

// The social sign-in providers ts-auth supports, in its order, with where the customer creates the OAuth client.
public sealed record AuthenticationProvider(string Key, string Name, string ClientIdLabel, string ConsoleName, string ConsoleUrl, string Help)
{
    public static AuthenticationProvider? Find(string key) => All.FirstOrDefault(provider => provider.Key == key);
    public static readonly AuthenticationProvider[] All =
    [
        new("google", "Google", "Client ID", "Google Cloud console credentials", "https://console.cloud.google.com/apis/credentials",
            "Create an OAuth client ID of type Web application and add the callback URL as an authorized redirect URI."),
        new("github", "GitHub", "Client ID", "GitHub Developer settings, OAuth Apps", "https://github.com/settings/developers",
            "Register a new OAuth App and set the callback URL as its authorization callback URL."),
        new("apple", "Apple", "Services ID", "Apple Developer Certificates, IDs & Profiles", "https://developer.apple.com/account/resources/identifiers/list/serviceId",
            "Create a Services ID with Sign in with Apple and the callback URL as a return URL, then a Sign in with Apple key. Paste the .p8 key below."),
        new("oidc", "OpenID Connect", "Client ID", "", "",
            "Any OpenID Connect provider, such as Auth0, Keycloak or another TerraScale project. Register the callback URL as a redirect URI.")
    ];
}
