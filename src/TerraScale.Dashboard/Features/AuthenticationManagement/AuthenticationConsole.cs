using System.Text.RegularExpressions;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
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
            var key = parts[0] == "settings" ? "sign-in" : parts[0];
            var known = parts switch
            {
                ["settings" or "branding" or "clients"] => true,
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
                        await projects.UpdateSettingsAsync(session, project, new(Branding: Branding(form["name"], form["logo_url"], form["theme_color"])), cancel);
                        break;
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
            return HttpRedirects.SeeOther("/auth/" + key);
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
        if (title is { Length: > 64 }) throw new AccountAccessException("Use a name of up to 64 characters.", 422);
        if (logo is not null && !(Uri.TryCreate(logo, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
            throw new AccountAccessException("The logo must be an https URL.", 422);
        if (color is not null && !Color().IsMatch(color)) throw new AccountAccessException("The theme color must look like #2158e8.", 422);
        return new(title, logo, color);
    }

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
        if (AuthenticationSection.Find(key) is null) return Results.NotFound();
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
        var query = http.Request.Query;
        var page = new AuthenticationPage(ShellModel.For(http, context, key == "overview" ? "Authentication" : "Authentication · " + section.Title), section,
            query["q"].ToString(), query["after"].ToString(), query["user"].ToString(), error, created);
        var (session, project, cancel) = (context.Session, context.Project!.Id, http.RequestAborted);
        try
        {
            switch (key)
            {
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
        new("clients", "OAuth applications", "Applications that sign users in through the authorization code flow with PKCE.", "Boxes", "Developers"),
        new("branding", "Branding", "Name, logo and color of the hosted sign-in pages.", "Wand2", "Customize")
    ];
}
