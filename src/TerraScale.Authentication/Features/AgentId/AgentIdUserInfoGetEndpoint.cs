
namespace TerraScale.Authentication.Features.AgentId;

public sealed class AgentIdUserInfoGetEndpoint(AgentIdTransactions transactions) : AgentIdUserInfoEndpoint(transactions)
{
    public override void Configure()
    {
        Get(AgentIdIssuer.UserInfoPath);
        this.Describe("agentid_userinfo");
    }
}
