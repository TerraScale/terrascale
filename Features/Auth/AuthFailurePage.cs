using TerraScale.Dashboard.UI;

namespace TerraScale.Dashboard.Features.Auth;

public sealed record AuthFailurePage(DocumentModel Document, string Message, string Reference, string ReturnTo)
{
    public string RetryUrl => "/login?prompt=login" + (ReturnTo == "/" ? "" : "&returnTo=" + Uri.EscapeDataString(ReturnTo));
    public string CodeUrl => "/login/code" + (ReturnTo == "/" ? "" : "?returnTo=" + Uri.EscapeDataString(ReturnTo));
}
