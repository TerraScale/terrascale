using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.Sso;

public sealed class VerifySsoDomainEndpoint(SsoConnections connections, PlatformDirectory platform, PlatformAuthentication authentication) : SsoEndpoint(platform, authentication)
{
    public override void Configure()
    {
        Post(Connection + "/verify");
        this.Describe("verify_sso_domain", "Verify the SSO connection's domain", "Needs the owner role. Looks up the TXT record named in verification; 409 domain_unverified until it carries the value. Once verified, no other organization can take the domain, and every account of the domain signs in through the identity provider, except the organization's owners, who keep email-code and password sign-in.", "Single sign-on", null, StatusCodes.Status200OK, typeof(SsoConnectionBody), conflict: true);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) =>
        await connections.VerifyAsync(await this.OrganizationAsync(caller, Roles.Owner, cancellationToken), caller.Id, cancellationToken) switch
        {
            SsoConnectionRecord verified => this.Ok(verified),
            AuthNotFound failure => ErrorResults.ToResult(failure),
            AuthConflict failure => ErrorResults.ToResult(failure),
        };
}
