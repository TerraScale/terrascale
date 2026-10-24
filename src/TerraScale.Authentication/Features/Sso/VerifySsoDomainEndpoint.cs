using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.Sso;

/// <summary><c>POST /v1/terrascale/organizations/{organization}/sso/verify</c>.</summary>
public sealed class VerifySsoDomainEndpoint(SsoConnections connections, PlatformDirectory platform, PlatformAuthentication authentication) : SsoEndpoint(platform, authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(Connection + "/verify");
        this.Document("verify_sso_domain", "Verify the SSO connection's domain", "Needs the owner role. Looks up the TXT record named in verification; 409 domain_unverified until it carries the value. Once verified, no other organization can take the domain.", "Single sign-on", null, StatusCodes.Status200OK, typeof(SsoConnectionBody), conflict: true);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) =>
        this.Ok(await connections.VerifyAsync(await this.OrganizationAsync(caller, Roles.Owner, cancellationToken), caller.Id, cancellationToken));
}
