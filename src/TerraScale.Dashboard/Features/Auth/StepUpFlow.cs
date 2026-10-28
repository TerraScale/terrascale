using Microsoft.Extensions.Primitives;
using TerraScale.Dashboard.Features.ApiKeys;
using TerraScale.Dashboard.Features.AuthenticationManagement;
using TerraScale.Dashboard.Features.Profile;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.Features.Auth;

public interface IAuthStepUpGateway
{
    Task<StepUpChallenge> BeginAsync(SessionRecord session, string purpose, string audience, string returnUri, CancellationToken cancellationToken);
    Task<string?> ConsumeAsync(SessionRecord session, string proof, string purpose, string audience, string? operationReceipt, CancellationToken cancellationToken);
}

public sealed record StepUpChallenge(string Proof, string StepUpUrl, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "Step-up challenge [REDACTED]";
}

// This intent exists only inside encrypted auth-flows. Its browser cookie contains
// a random correlation bearer, never the proof. Taking the intent is single use.
public sealed record StepUpIntent(string UserId, string Browser, string? OrganizationId, string? ProjectId, string Purpose, string Audience,
    string Path, string Query, Dictionary<string, string[]> Form, StepUpChallenge Challenge, string? Receipt = null, McpAuthorization? Mcp = null)
{
    public override string ToString() => "Step-up intent [REDACTED]";
}

public sealed partial class AuthFlows
{
    private const string StepUpCookie = "ts-dashboard-step-up";

    public async Task StoreStepUpAsync(HttpContext http, StepUpIntent intent)
    {
        await EndAsync(http, StepUpCookie);
        await StoreAsync(http, StepUpCookie, intent, intent.Challenge.ExpiresAt);
    }

    public async Task<StepUpIntent?> TakeStepUpAsync(HttpContext http)
    {
        var stored = await ReadAsync<StepUpIntent>(http, StepUpCookie);
        http.Response.Cookies.Delete(StepUpCookie, Options(http));
        if (stored is null) return null;
        try { await state.DeleteAsync(Collection, stored.Id, stored.Version, http.RequestAborted); }
        catch (HttpRequestException error) when (error.StatusCode is System.Net.HttpStatusCode.PreconditionFailed or System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.NotFound)
        { return null; }
        return stored.Value.Challenge.ExpiresAt > time.GetUtcNow() ? stored.Value : null;
    }
}

public static class StepUpFlow
{
    private static readonly object IntentKey = new();
    public static AccountAccessException Required() => new("Confirm this action from the dashboard using your password or email code and, when enrolled, two-factor authentication. Start the action again if confirmation expired.", 403, "step_up_required");
    public static bool IsResuming(HttpContext http) => http.Items.ContainsKey(IntentKey);

    public static StepUpIntent? Intent(HttpContext http, AccountContext context, string purpose)
    {
        if (!http.Items.TryGetValue(IntentKey, out var value)) return null;
        var intent = (StepUpIntent)value!;
        Validate(http, context, intent, purpose);
        return intent;
    }

    public static void Validate(HttpContext http, AccountContext context, StepUpIntent intent, string purpose)
    {
        if (intent.Purpose != purpose || intent.UserId != context.UserId || intent.Browser != Browser(http) ||
            intent.OrganizationId != context.Organization?.Id || intent.ProjectId != context.Project?.Id ||
            intent.Challenge.ExpiresAt <= http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow()) throw Required();
    }

    public static async Task<IResult> StartAsync(HttpContext http, AccountContext context, string purpose, string audience, string? receipt = null, McpAuthorization? mcp = null)
    {
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        // API clients receive an actionable refusal; proofs must never be submitted in forms or URLs.
        if (!http.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase) && http.Request.Headers["Sec-Fetch-Mode"] != "navigate") throw Required();
        var challenge = await http.RequestServices.GetRequiredService<IAuthStepUpGateway>().BeginAsync(context.Session, purpose, audience,
            $"{http.Request.Scheme}://{http.Request.Host}/auth/step-up/callback", http.RequestAborted);
        if (challenge.Proof.Length is 0 or > 4096 || challenge.ExpiresAt <= http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow() ||
            !Uri.TryCreate(challenge.StepUpUrl, UriKind.Absolute, out var url) || url.Scheme != "https" || !url.IsDefaultPort ||
            url.Host != "auth.terrascale.tech" || url.UserInfo.Length != 0 || url.Fragment.Length != 0 || challenge.StepUpUrl.Contains(challenge.Proof, StringComparison.Ordinal))
            throw new IOException("ts-auth returned an invalid confirmation challenge.");
        await http.RequestServices.GetRequiredService<AuthFlows>().StoreStepUpAsync(http, new(context.UserId, Browser(http), context.Organization?.Id,
            context.Project?.Id, purpose, audience, http.Request.Path, ScopeQuery(http, context), form.ToDictionary(pair => pair.Key, pair => pair.Value.Select(value => value ?? "").ToArray()), challenge, receipt, mcp));
        return Results.Redirect(challenge.StepUpUrl);
    }

    public static async Task<string?> ConsumeAsync(HttpContext http, AccountContext context, StepUpIntent intent)
    {
        Validate(http, context, intent, intent.Purpose);
        return await http.RequestServices.GetRequiredService<IAuthStepUpGateway>().ConsumeAsync(context.Session, intent.Challenge.Proof, intent.Purpose,
            intent.Audience, intent.Receipt, http.RequestAborted);
    }

    public static async Task<IResult> CallbackAsync(HttpContext http, AuthFlows flows, DashboardSessions sessions, AccountService accounts)
    {
        try
        {
            if (http.Request.QueryString.HasValue) throw Required();
            var intent = await flows.TakeStepUpAsync(http) ?? throw Required();
            var session = await sessions.ReadAsync(http);
            if (session?.UserId != intent.UserId || Browser(http) != intent.Browser) throw Required();
            http.Request.QueryString = new(intent.Query);
            var context = await AccountRequest.RequireAsync(http, sessions, accounts) ?? throw Required();
            http.Items[IntentKey] = intent;
            _ = Intent(http, context, intent.Purpose);
            // The one-use encrypted intent and original browser binding authorize this
            // continuation. The cross-site callback does not depend on a Strict CSRF cookie.
            http.Request.Method = "POST";
            http.Request.Path = intent.Path;
            http.Request.ContentType = "application/x-www-form-urlencoded";
            http.Request.Form = new FormCollection(intent.Form.ToDictionary(pair => pair.Key, pair => new StringValues(pair.Value)));
            var services = http.RequestServices;
            return intent.Purpose switch
            {
                "delete_account" => await AccountDeletion.DeleteAsync(http, sessions, accounts, services.GetRequiredService<ITsAuthPlatform>(), services.GetRequiredService<TerraScale.Dashboard.Platform.Persistence.IDashboardState>(), services.GetRequiredService<AccessTokens>(), services.GetRequiredService<TerraScale.Dashboard.Features.Billing.StripeBilling>(), services.GetRequiredService<TerraScale.Dashboard.Platform.Persistence.IDashboardIdentity>()),
                "create_pat" => await AuthEndpoints.AuthorizeAsync(http, "/profile?tab=access-tokens", intent.Form.GetValueOrDefault("name")?.SingleOrDefault() ?? "", intent.Mcp is null ? int.Parse(intent.Form["expiresInDays"].Single(), System.Globalization.CultureInfo.InvariantCulture) : 0, mcp: intent.Mcp, stepUp: intent),
                "create_api_key" => await ApiKeyEndpoints.CreateAsync(http, "api-key", sessions, accounts, services.GetRequiredService<ApiKeyService>()),
                "rotate_api_key" => await ApiKeyEndpoints.RotateAsync(http, sessions, accounts, services.GetRequiredService<ApiKeyService>()),
                "create_oauth_client" => await AuthenticationConsole.PostAsync(http, "clients", sessions, accounts, services.GetRequiredService<ITsAuthProjects>()),
                _ => throw Required()
            };
        }
        catch (AccountAccessException error) { return Results.Problem(error.Message, statusCode: error.StatusCode, extensions: new Dictionary<string, object?> { ["code"] = error.Code }); }
        catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException)
        { return Results.Problem("Confirmation could not finish. Start the action again; no new action will be authorized automatically.", statusCode: 503); }
    }

    private static string ScopeQuery(HttpContext http, AccountContext context)
    {
        var query = http.Request.Query.ToDictionary(pair => pair.Key, pair => (string?)pair.Value.ToString());
        if (context.Organization is { } organization) query["organizationId"] = organization.Id;
        if (context.Project is { } project) query["projectId"] = project.Id;
        return QueryString.Create(query).Value ?? "";
    }

    public static string Browser(HttpContext http)
    {
        var cookie = http.Request.Cookies["ts-dashboard-session"];
        if (cookie is not { Length: 64 } || !cookie.All(char.IsAsciiHexDigitLower)) throw Required();
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(cookie)));
    }
}
