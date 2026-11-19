using TerraScale.Authentication.Features.Agents;

namespace TerraScale.Authentication.Features.AgentId;

public sealed class AgentIdUserInfoGetEndpoint(LiveAgents agents) : AgentIdUserInfoEndpoint(agents)
{
    public override void Configure()
    {
        Get(AgentIdIssuer.UserInfoPath);
        this.Describe("agentid_userinfo");
    }
}
