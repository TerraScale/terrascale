using OpenIddict.Abstractions;
using TerraScale.Authentication.Configuration;

namespace TerraScale.Authentication.Features.AgentId;

public sealed class AgentIdAuthorizeGetEndpoint(AgentIdTransactions transactions, IOpenIddictApplicationManager applications, AuthOptions options)
    : AgentIdAuthorizeEndpoint(transactions, applications, options)
{
    public override void Configure()
    {
        Get(AgentIdIssuer.AuthorizePath);
        this.Describe("agentid_authorize");
    }
}
