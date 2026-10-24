using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Organizations.Sso;

public sealed record SsoConnection(string OrganizationId, string Domain, bool DomainVerified, DateTimeOffset? VerifiedAt, SsoVerification Verification,
    string Protocol, string DefaultRole, SsoOidc? Oidc, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record SsoVerification(string Type, string Name, string Value);
public sealed record SsoOidc(string Issuer, string ClientId, bool ClientSecretSet, string RedirectUri);
// OIDC only: ts-auth refuses a saml member until its SAML support ships.
public sealed record SsoUpdate(string Domain, string Protocol, string DefaultRole, SsoOidcInput Oidc);
public sealed record SsoOidcInput(string Issuer, string ClientId, string? ClientSecret)
{
    public override string ToString() => $"OIDC {Issuer} {ClientId} [REDACTED]";
}
// The page's view: the connection (null when the organization has none) or why it could not be read.
public sealed record OrganizationSsoView(SsoConnection? Connection, bool PlanIncludesSso, string Unavailable = "");

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SsoConnection))]
[JsonSerializable(typeof(SsoUpdate))]
internal sealed partial class SsoJson : JsonSerializerContext;

// ts-auth owns SSO connections. ts-dotnet 0.0.6 has no SSO wrappers yet, so these call ts-auth's
// /v1/terrascale/organizations/{org}/sso routes with the signed-in user's own access token, as the
// SDK's platform calls do. ts-auth rechecks the caller's role; the dashboard adds the plan gate.
public sealed class OrganizationSsoService(IHttpClientFactory http)
{
    private static readonly Uri AuthOrigin = new("https://auth.terrascale.tech/");

    // Single sign-on is a Team and Enterprise feature. An internal billing policy copies a paid plan's
    // contract, so it counts by that base plan. An organization without an active plan has none.
    public static bool PlanIncludesSso(OrganizationRecord organization) =>
        organization.BillingStatus != "billing_required" && PlanCatalog.For(organization) is { } plan &&
        (plan.Key == "internal" ? organization.InternalBilling!.BasePlanKey : plan.Key) is "team" or "enterprise";

    public async Task<OrganizationSsoView> ReadAsync(AccountContext context, CancellationToken cancellationToken)
    {
        var included = PlanIncludesSso(context.Organization!);
        try { return new(await SendAsync(context, HttpMethod.Get, "", null, cancellationToken), included); }
        catch (AccountAccessException error) { return new(null, included, error.Message); }
        catch (Exception error) when (error is HttpRequestException or JsonException or IOException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        { return new(null, included, "Single sign-on settings are unavailable right now. Reload the page to try again."); }
    }

    public async Task SaveAsync(AccountContext context, SsoUpdate update, CancellationToken cancellationToken)
    {
        Change(context, planned: true);
        await SendAsync(context, HttpMethod.Put, "", update, cancellationToken);
    }

    public async Task VerifyAsync(AccountContext context, CancellationToken cancellationToken)
    {
        Change(context, planned: true);
        await SendAsync(context, HttpMethod.Post, "/verify", null, cancellationToken);
    }

    // Removal is open on every plan, so a downgraded organization can still take its connection down.
    public async Task DeleteAsync(AccountContext context, CancellationToken cancellationToken)
    {
        Change(context, planned: false);
        await SendAsync(context, HttpMethod.Delete, "", null, cancellationToken);
    }

    private static void Change(AccountContext context, bool planned)
    {
        if (context.Organization is null) throw new AccountAccessException("Select an organization.");
        if (context.Role != "owner") throw new AccountAccessException("Only the organization owner can change single sign-on.");
        if (planned && !PlanIncludesSso(context.Organization))
            throw new AccountAccessException("Single sign-on needs the Team or Enterprise plan. Upgrade on the Billing tab.");
    }

    private async Task<SsoConnection?> SendAsync(AccountContext context, HttpMethod method, string suffix, SsoUpdate? body, CancellationToken cancellationToken)
    {
        var organizationId = context.Organization?.Id is { } id && TerraScaleIds.IsValid(id) ? id : throw new AccountAccessException("Select an organization.");
        if (string.IsNullOrWhiteSpace(context.Session.AccessToken)) throw new AccountAccessException("Sign in again to continue.", 401);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(method, new Uri(AuthOrigin, $"/v1/terrascale/organizations/{organizationId}/sso{suffix}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.Session.AccessToken);
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null) request.Content = JsonContent.Create(body, SsoJson.Default.SsoUpdate);
        using var response = await http.CreateClient("platform").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        if (response.StatusCode == HttpStatusCode.NotFound && method == HttpMethod.Get) return null;
        using var document = await PlatformJson.ReadAsync(response.Content, deadline.Token);
        if ((int)response.StatusCode is >= 400 and < 500) throw Refusal(response.StatusCode, document.RootElement);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("ts-auth could not answer the single sign-on request.", null, response.StatusCode);
        var connection = document.RootElement.Deserialize(SsoJson.Default.SsoConnection);
        return connection?.OrganizationId == organizationId ? connection : throw new HttpRequestException("ts-auth returned a connection for another organization.");
    }

    private static AccountAccessException Refusal(HttpStatusCode status, JsonElement body)
    {
        var error = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
        string Text(string name) => error.ValueKind == JsonValueKind.Object && error.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()! : "";
        var code = Text("code");
        var message = code switch
        {
            // ts-auth names the field it refused; the text is escaped when rendered.
            "invalid_request" when Text("message") is { Length: > 0 and <= 300 } field => "ts-auth refused the settings: " + field,
            "domain_unverified" => "The TXT record was not found yet. DNS changes can take a while to spread; try again in a few minutes.",
            "conflict" => "Another organization has verified this domain, or this organization's connection uses a different domain. Remove the existing connection first.",
            _ => status switch
            {
                HttpStatusCode.Unauthorized => "Sign in again to continue.",
                HttpStatusCode.Forbidden => "Your role does not allow this change.",
                HttpStatusCode.NotFound => "Single sign-on is unavailable for this organization right now.",
                HttpStatusCode.TooManyRequests => "Too many requests. Try again shortly.",
                _ => "ts-auth refused the single sign-on change."
            }
        };
        return new(message, status == HttpStatusCode.BadRequest ? StatusCodes.Status422UnprocessableEntity : (int)status, code);
    }

    // Reads the configure form. The client secret is write-only: left blank, ts-auth keeps the stored one.
    public static SsoUpdate Parse(IFormCollection form)
    {
        string Field(string name, int max = 2048) => form[name].ToString().Trim() is var value && value.Length <= max
            ? value : throw new AccountAccessException("A single sign-on field is too long.", 422);
        static string Https(string value, string label) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0
            ? value : throw new AccountAccessException(label + " must be an https:// URL.", 422);
        static string Required(string value, string label) => value.Length > 0 ? value : throw new AccountAccessException("Enter the " + label + ".", 422);
        var domain = Field("domain", 253).ToLowerInvariant();
        if (Uri.CheckHostName(domain) != UriHostNameType.Dns || !domain.Contains('.')) throw new AccountAccessException("Enter the email domain, for example acme.com.", 422);
        var role = Field("default_role");
        if (role is not ("viewer" or "member" or "admin")) throw new AccountAccessException("Choose a default role.", 422);
        return new(domain, "oidc", role, new(Https(Field("oidc_issuer"), "The issuer"), Required(Field("oidc_client_id"), "client ID"),
            Field("oidc_client_secret", 1024) is { Length: > 0 } secret ? secret : null));
    }
}

public static class OrganizationSsoEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationSso(this IEndpointRouteBuilder endpoints)
    {
        foreach (var route in new[] { "", "/verify", "/delete" })
        {
            var operation = route;
            endpoints.MapPost("/api/v1/dashboard/organization/sso" + operation,
                async (HttpContext http, DashboardSessions sessions, AccountService accounts, OrganizationSsoService sso, IWorkspacePlacement placement) =>
                {
                    var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                    if (context is null) return Results.Unauthorized();
                    var form = await http.Request.ReadFormAsync(http.RequestAborted);
                    if (!Csrf.Validate(http, form)) return Results.BadRequest("Invalid form token.");
                    try
                    {
                        if (operation == "/verify") await sso.VerifyAsync(context, http.RequestAborted);
                        else if (operation == "/delete") await sso.DeleteAsync(context, http.RequestAborted);
                        else await sso.SaveAsync(context, OrganizationSsoService.Parse(form), http.RequestAborted);
                        return HttpRedirects.SeeOther("/organization?tab=sso");
                    }
                    catch (AccountAccessException error) { return await OrganizationEndpoints.PageAsync(http, context, placement, error.Message, error.StatusCode, "sso"); }
                    catch (Exception error) when (error is HttpRequestException or JsonException or IOException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
                    { return await OrganizationEndpoints.PageAsync(http, context, placement, "Single sign-on is unavailable right now. Try again shortly.", 503, "sso"); }
                });
        }
        return endpoints;
    }
}
