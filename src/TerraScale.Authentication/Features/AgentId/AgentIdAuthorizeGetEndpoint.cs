using TerraScale.Authentication.Configuration;

namespace TerraScale.Authentication.Features.AgentId;

public sealed class AgentIdAuthorizeGetEndpoint(AgentIdTransactions transactions, AuthOptions options) : AgentIdAuthorizeEndpoint(transactions, options)
{
    public override void Configure()
    {
        Get(AgentIdIssuer.AuthorizePath);
        this.Describe("agentid_authorize");
    }
}
