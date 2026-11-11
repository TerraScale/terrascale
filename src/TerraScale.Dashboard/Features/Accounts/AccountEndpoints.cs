using System.Text.Json.Serialization;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Accounts
{
    public static class AccountEndpoints
    {
        public static IServiceCollection AddAccountFeatures(this IServiceCollection services)
        {
            return services.AddSingleton<AccountStore>().AddSingleton<OrganizationStore>().AddSingleton<AccountService>();
        }

        internal static async Task<IResult> SwitchAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, string field,
            Func<SessionRecord, string, CancellationToken, Task<AccountContext>> select)
        {
            var session = await sessions.ReadAsync(http);
            if (session is null)
            {
                return Results.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, "/", Results.BadRequest("Invalid form token."));
            }

            AccountContext selected;
            try { selected = await select(session, form[field].ToString(), http.RequestAborted); }
            catch (AccountAccessException error) { return Results.Problem(error.Message, statusCode: error.StatusCode); }
            return HttpRedirects.SeeOther(SwitchReturnPath(form["returnUrl"].ToString(), selected));
        }

        private static string SwitchReturnPath(string? returnUrl, AccountContext selected)
        {
            var path = Csrf.LocalRedirect(returnUrl);
            var route = path.Split('?', '#')[0];
            const string prefix = "/databases/";
            if (!route.StartsWith(prefix, StringComparison.Ordinal))
            {
                return WorkspaceUrl.For(selected, path, replace: true);
            }

            var databaseId = Uri.UnescapeDataString(route[prefix.Length..].Split('/')[0]);
            return WorkspaceUrl.For(selected, selected.Project?.Databases.ContainsKey(databaseId) == true ? path : "/databases", replace: true);
        }

        public static async Task<AccountContext?> RequireAsync(HttpContext http, DashboardSessions sessions, AccountService accounts)
        {
            return await AccountRequest.RequireAsync(http, sessions, accounts);
        }

        public static IResult LoginRedirect(HttpContext http)
        {
            return AccountRequest.LoginRedirect(http);
        }
    }

    public sealed record AccountIdentityResponse(string UserId, string AccountId, string Name, string Email,
        string PhoneNumber, string Locale, bool OnboardingCompleted, string? OrganizationId, string? ProjectId, string Role);

    public sealed record SwitchOrganizationRequest(
        [property: JsonPropertyName("_csrf")] string Csrf, string OrganizationId, string? ReturnUrl);

    public sealed record SwitchProjectRequest(
        [property: JsonPropertyName("_csrf")] string Csrf, string ProjectId, string? ReturnUrl);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(AccountIdentityResponse))]
    [JsonSerializable(typeof(SwitchOrganizationRequest))]
    [JsonSerializable(typeof(SwitchProjectRequest))]
    internal sealed partial class AccountContractJson : JsonSerializerContext;
}
