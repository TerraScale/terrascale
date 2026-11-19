
namespace TerraScale.Authentication.Features.AgentId;

public sealed class AgentIdUserInfoPostEndpoint(AgentIdTransactions transactions, AgentIdClients clients) : AgentIdUserInfoEndpoint(transactions, clients)
{
    public override void Configure()
    {
        Post(AgentIdIssuer.UserInfoPath);
        this.Describe(PostRouteName);
        this.AcceptsForm();
    }
}
