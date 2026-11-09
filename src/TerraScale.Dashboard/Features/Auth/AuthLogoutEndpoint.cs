using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Auth
{
    public sealed class AuthLogoutEndpoint : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/auth/logout");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(AuthEndpoints.LogoutAsync(HttpContext));
    }
}
