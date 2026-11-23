using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Billing
{
    public sealed record BillingRecoveryModel(DocumentModel Document, string Heading, string Detail)
    {
        internal static IResult Response(HttpContext http, int status, string heading, string detail)
        {
            var page = BillingRecovery.Create(new BillingRecoveryModel(new DocumentModel(heading, DocumentModel.NonceFor(http)), heading, detail));
            page.StatusCode = status;
            return page;
        }
    }
}
