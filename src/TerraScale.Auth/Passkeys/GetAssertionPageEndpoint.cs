namespace TerraScale.Auth.Passkeys;

using System.Net;
using System.Text;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Auth.Users;
using TerraScale.Auth.WebAuthn;
using TerraScale.DotNet;
using TerraScale.Endpoints;
using static TerraScale.Auth.Passkeys.PasskeySupport;

internal sealed class GetAssertionPageEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/ui/{slug}/passkeys/assert", Handle);

    private static IResult Handle(string slug)
        => PasskeyPages.Assert(slug);
}
