
namespace TerraScale.Authentication.Features.AgentId;

public sealed class AgentIdUserInfoPostEndpoint(AgentIdTransactions transactions) : AgentIdUserInfoEndpoint(transactions)
{
    public override void Configure()
    {
        Post(AgentIdIssuer.UserInfoPath);
        this.Describe("agentid_userinfo_post");
        this.AcceptsForm();
    }
}
