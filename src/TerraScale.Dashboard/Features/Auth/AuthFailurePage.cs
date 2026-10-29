using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.Auth;

public sealed record AuthFailurePage(DocumentModel Document, string Message, string Reference, string ReturnTo)
{
    public string RetryUrl => "/login" + (ReturnTo == "/" ? "" : "?returnTo=" + Uri.EscapeDataString(ReturnTo));
}
