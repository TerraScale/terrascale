using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations.Audit
{
    public sealed class AuditRedirectEndpoint : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/audit");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(HttpRedirects.SeeOther("/organization?tab=audit" + (HttpContext.Request.QueryString.HasValue ? "&" + HttpContext.Request.QueryString.Value![1..] : "")));
    }
}
