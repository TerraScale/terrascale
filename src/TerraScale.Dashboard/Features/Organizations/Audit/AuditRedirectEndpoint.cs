using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations.Audit
{
    public sealed class AuditRedirectEndpoint : EndpointWithoutRequest<IResult>
    {
        private static readonly string[] Filters = ["search", "action", "severity", "range", "page"];

        public override void Configure() => Get("/audit");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var query = HttpContext.Request.Query;
            var forwarded = Filters.Where(key => query[key].ToString() is { Length: > 0 and <= 200 })
                .Select(key => KeyValuePair.Create(key, (string?)query[key].ToString()));
            return new(HttpRedirects.SeeOther(QueryHelpers.AddQueryString("/organization?tab=audit", forwarded)));
        }
    }
}
