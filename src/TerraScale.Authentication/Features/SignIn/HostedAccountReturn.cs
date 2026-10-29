using TerraScale.Authentication.Features.Oidc;

namespace TerraScale.Authentication.Features.SignIn;

/// <summary>The platform account's exact, first-party security continuation.</summary>
internal static class HostedAccountReturn
{
    internal const string AccountUrl = "https://dash.terrascale.tech/profile?tab=security";

    internal static string SecurityUrl => SignInFlow.SecurityPath(OidcIssuer.InstanceId) + "?next=" + Uri.EscapeDataString(AccountUrl);

    internal static bool Allowed(string tenant, string? next) => tenant == OidcIssuer.InstanceId && (next == AccountUrl || next == SecurityUrl);

    internal static string? Next(HttpContext context) => SignInFlow.SafeNext(context,
        context.Request.HasFormContentType ? context.Request.Form["next"] : context.Request.Query["next"]);

    internal static string Security(HttpContext context) =>
        Next(context) == AccountUrl ? SecurityUrl : SignInFlow.SecurityPath(SignInFlow.Tenant(context));
}
