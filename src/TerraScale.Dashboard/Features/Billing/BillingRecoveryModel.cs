using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.Billing;

public sealed record BillingRecoveryModel(DocumentModel Document, string Heading, string Detail)
{
    // Browser navigations get a branded page back to billing; machine callers keep the caller's original response.
    internal static IResult Response(HttpContext http, int status, string heading, string detail)
    {
        var page = BillingRecovery.Create(new BillingRecoveryModel(new DocumentModel(heading, DocumentModel.NonceFor(http)), heading, detail));
        page.StatusCode = status;
        return page;
    }
}
