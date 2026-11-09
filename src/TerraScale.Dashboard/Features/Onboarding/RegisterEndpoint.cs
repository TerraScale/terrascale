using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Onboarding
{
    public sealed class RegisterEndpoint : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/register");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(HttpRedirects.SeeOther("/onboarding" + HttpContext.Request.QueryString));
    }
}
