using OpenIddict.Abstractions;
using TerraScale.Authentication.Configuration;

namespace TerraScale.Authentication.Features.AgentId;

public sealed class AgentIdAuthorizePostEndpoint(AgentIdTransactions transactions, IOpenIddictApplicationManager applications, AuthOptions options)
    : AgentIdAuthorizeEndpoint(transactions, applications, options)
{
    public override void Configure()
    {
        Post(AgentIdIssuer.AuthorizePath);
        this.Describe("agentid_authorize_post");
        this.AcceptsForm();
    }
}
