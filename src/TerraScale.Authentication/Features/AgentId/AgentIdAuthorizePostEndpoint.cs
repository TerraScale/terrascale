using TerraScale.Authentication.Configuration;

namespace TerraScale.Authentication.Features.AgentId;

public sealed class AgentIdAuthorizePostEndpoint(AgentIdTransactions transactions, AuthOptions options) : AgentIdAuthorizeEndpoint(transactions, options)
{
    public override void Configure()
    {
        Post(AgentIdIssuer.AuthorizePath);
        this.Describe("agentid_authorize_post");
        this.AcceptsForm();
    }
}
