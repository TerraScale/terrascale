
namespace TerraScale.Authentication.Features.AgentId;

public sealed class AgentIdUserInfoGetEndpoint(AgentIdTransactions transactions, AgentIdClients clients) : AgentIdUserInfoEndpoint(transactions, clients)
{
    public override void Configure()
    {
        Get(AgentIdIssuer.UserInfoPath);
        this.Describe(RouteName);
    }
}
