using TerraScale.Dashboard.Shared.UI;
namespace TerraScale.Dashboard.Features.Auth;

public sealed record DevicePage(DocumentModel Document, string CsrfToken, string Code = "", string Email = "", string Error = "", string Result = "", bool Pending = false, bool Authenticated = false)
{
    public string SignInUrl => "/login?returnTo=" + Uri.EscapeDataString("/cli/approve?user_code=" + Uri.EscapeDataString(Code));
}
