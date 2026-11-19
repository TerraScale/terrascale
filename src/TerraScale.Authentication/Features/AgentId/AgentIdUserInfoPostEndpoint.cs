using TerraScale.Authentication.Features.Agents;

namespace TerraScale.Authentication.Features.AgentId;

public sealed class AgentIdUserInfoPostEndpoint(LiveAgents agents) : AgentIdUserInfoEndpoint(agents)
{
    public override void Configure()
    {
        Post(AgentIdIssuer.UserInfoPath);
        this.Describe("agentid_userinfo_post");
        this.AcceptsForm();
    }
}
