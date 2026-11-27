using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Dashboard.Common.Accounts;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Organizations.Sso
{
    public sealed record SsoConnection(string OrganizationId, string Domain, bool DomainVerified, DateTimeOffset? VerifiedAt, SsoVerification Verification,
        string Protocol, string DefaultRole, SsoOidc? Oidc, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    public sealed record SsoVerification(string Type, string Name, string Value);
    public sealed record SsoOidc(string Issuer, string ClientId, bool ClientSecretSet, string RedirectUri);
    public sealed record SsoUpdate(string Domain, string Protocol, string DefaultRole, SsoOidcInput Oidc);
    public sealed record SsoOidcInput(string Issuer, string ClientId, string? ClientSecret)
    {
        public override string ToString()
        {
            return $"OIDC {Issuer} {ClientId} [REDACTED]";
        }
    }
    public readonly union SsoUpdateInput(SsoUpdate, DashboardValidation);

    public sealed record OrganizationSsoView(SsoConnection? Connection, bool PlanIncludesSso, string Unavailable = "");

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(SsoConnection))]
    [JsonSerializable(typeof(SsoUpdate))]
    internal sealed partial class SsoJson : JsonSerializerContext;

    public sealed class OrganizationSsoService(IHttpClientFactory http)
    {
        public static bool PlanIncludesSso(OrganizationRecord organization)
        {
            return PlanCatalog.IsTerraScale(organization) || organization.BillingStatus != "billing_required" && PlanCatalog.For(organization) is { } plan &&
            (plan.Key == "internal" ? organization.InternalBilling?.BasePlanKey : plan.Key) is "team" or "enterprise";
        }

        public async Task<OrganizationSsoView> ReadAsync(AccountContext context, CancellationToken cancellationToken)
        {
            var included = context.Organization is { } organization && PlanIncludesSso(organization);
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

        public async Task DeleteAsync(AccountContext context, CancellationToken cancellationToken)
        {
            Change(context, planned: false);
            await SendAsync(context, HttpMethod.Delete, "", null, cancellationToken);
        }

        private static void Change(AccountContext context, bool planned)
        {
            if (context.Organization is null)
            {
                throw new AccountAccessException("Select an organization.");
            }

            if (context.Role != "owner")
            {
                throw new AccountAccessException("Only the organization owner can change single sign-on.");
            }

            if (planned && !PlanIncludesSso(context.Organization))
            {
                throw new AccountAccessException("Single sign-on needs the Team or Enterprise plan. Upgrade on the Billing tab.");
            }
        }

        private async Task<SsoConnection?> SendAsync(AccountContext context, HttpMethod method, string suffix, SsoUpdate? body, CancellationToken cancellationToken)
        {
            var organizationId = context.Organization?.Id is { } id && TerraScaleIds.IsValid(id) ? id : throw new AccountAccessException("Select an organization.");
            if (string.IsNullOrWhiteSpace(context.Session.AccessToken))
            {
                throw new AccountAccessException("Sign in again to continue.", 401);
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(method, new Uri(TsAuthOrigin.Value, $"/v1/terrascale/organizations/{organizationId}/sso{suffix}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.Session.AccessToken);
            request.Headers.Accept.ParseAdd("application/json");
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, SsoJson.Default.SsoUpdate);
            }

            using var response = await http.CreateClient("platform").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return null;
            }

            if (response.StatusCode == HttpStatusCode.NotFound && method == HttpMethod.Get)
            {
                return null;
            }

            using var json = await PlatformJson.ReadAsync(response.Content, deadline.Token);
            if ((int)response.StatusCode is >= 400 and < 500)
            {
                throw Refusal(response.StatusCode, json.RootElement);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException("ts-auth could not answer the single sign-on request.", null, response.StatusCode);
            }

            var connection = json.RootElement.Deserialize(SsoJson.Default.SsoConnection);
            return connection?.OrganizationId == organizationId ? connection : throw new HttpRequestException("ts-auth returned a connection for another organization.");
        }

        private static AccountAccessException Refusal(HttpStatusCode status, JsonElement body)
        {
            var error = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
            string Text(string name)
            {
                return error.ValueKind == JsonValueKind.Object && error.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String && item.GetString() is { } text ? text : "";
            }

            var code = Text("code");
            var message = code switch
            {
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

        public static SsoUpdateInput Parse(IFormCollection form)
        {
            string? Field(string name, int max = 2048) =>
                form[name].ToString().Trim() is var trimmed && trimmed.Length <= max ? trimmed : null;

            static bool Https(string value) =>
                Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0;

            var tooLong = new DashboardValidation("A single sign-on field is too long.");
            if (Field("domain", 253) is not { } rawDomain)
            {
                return tooLong;
            }

            var domain = rawDomain.ToLowerInvariant();
            if (Uri.CheckHostName(domain) != UriHostNameType.Dns || !domain.Contains('.'))
            {
                return new DashboardValidation("Enter the email domain, for example acme.com.");
            }

            if (Field("default_role") is not { } role)
            {
                return tooLong;
            }

            if (role is not ("viewer" or "member" or "admin"))
            {
                return new DashboardValidation("Choose a default role.");
            }

            if (Field("oidc_issuer") is not { } issuer)
            {
                return tooLong;
            }

            if (!Https(issuer))
            {
                return new DashboardValidation("The issuer must be an https:// URL.");
            }

            if (Field("oidc_client_id") is not { } clientId)
            {
                return tooLong;
            }

            if (clientId.Length == 0)
            {
                return new DashboardValidation("Enter the client ID.");
            }

            if (Field("oidc_client_secret", 1024) is not { } secret)
            {
                return tooLong;
            }

            return new SsoUpdate(domain, "oidc", role, new(issuer, clientId, secret is { Length: > 0 } text ? text : null));
        }
    }

    public static class OrganizationSsoEndpoints
    {
        internal static async Task<IResult> MutateAsync(HttpContext http, DashboardSessions sessions, AccountService accounts,
            OrganizationSsoService sso, IWorkspacePlacement placement, string operation)
        {
            var required = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (!AccountRequest.TryResolveApi(http, required, out var context, out var rejection))
            {
                return rejection;
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, "/organization?tab=sso", TypedResults.BadRequest("Invalid form token."));
            }

            try
            {
                if (operation == "/verify")
                {
                    await sso.VerifyAsync(context, http.RequestAborted);
                }
                else if (operation == "/delete")
                {
                    await sso.DeleteAsync(context, http.RequestAborted);
                }
                else
                {
                    var parsed = OrganizationSsoService.Parse(form);
                    var invalid = parsed switch
                    {
                        DashboardValidation error => error,
                        SsoUpdate update => await SaveAsync(update),
                    };
                    if (invalid is not null)
                    {
                        if (http.Request.IsDatastarRequest)
                        {
                            await OrganizationEndpoints.PatchAsync(http, context, placement, invalid.Message, "sso");
                            return TypedResults.Empty;
                        }
                        return await OrganizationEndpoints.PageAsync(http, context, placement, invalid.Message, 422, "sso");
                    }
                }

                if (http.Request.IsDatastarRequest)
                {
                    context = await AccountScope.RefreshAsync(accounts, context, http.RequestAborted, reload: true);
                    await DatastarEvents.PatchElementsAsync(http, OrganizationPageView.Create(await OrganizationEndpoints.PageModelAsync(http, context, placement, tabOverride: "sso")));
                    return TypedResults.Empty;
                }
                return HttpRedirects.SeeOther("/organization?tab=sso");
            }
            catch (AccountAccessException error)
            {
                if (http.Request.IsDatastarRequest)
                {
                    await OrganizationEndpoints.PatchAsync(http, context, placement, error.Message, "sso");
                    return TypedResults.Empty;
                }
                return await OrganizationEndpoints.PageAsync(http, context, placement, error.Message, error.StatusCode, "sso");
            }
            catch (Exception error) when (error is HttpRequestException or JsonException or IOException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
            {
                if (http.Request.IsDatastarRequest)
                {
                    await OrganizationEndpoints.PatchAsync(http, context, placement, "Single sign-on is unavailable right now. Try again shortly.", "sso");
                    return TypedResults.Empty;
                }
                return await OrganizationEndpoints.PageAsync(http, context, placement, "Single sign-on is unavailable right now. Try again shortly.", 503, "sso");
            }

            async Task<DashboardValidation?> SaveAsync(SsoUpdate update)
            {
                await sso.SaveAsync(context, update, http.RequestAborted);
                return null;
            }
        }

    }
}
