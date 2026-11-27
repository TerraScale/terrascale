using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Auth
{
    public static class OrganizationPicker
    {
        public static async Task<IResult> PageAsync(HttpContext http, DashboardSessions sessions, AccountService accounts)
        {
            var required = await AccountRequest.RequireAsync(http, sessions, accounts, organizationScoped: false);
            if (!AccountRequest.TryResolvePage(http, required, out var context, out var rejection))
            {
                return rejection;
            }

            var returnTo = Csrf.LocalRedirect(http.Request.Query["returnTo"].ToString());
            return OrganizationPickerView.Create(new(new("Choose an organization", DocumentModel.NonceFor(http), Theme: "system"), context, Csrf.Token(http), returnTo));
        }
    }

    public sealed record OrganizationPickerPage(DocumentModel Document, AccountContext Context, string CsrfToken, string ReturnTo);
}
