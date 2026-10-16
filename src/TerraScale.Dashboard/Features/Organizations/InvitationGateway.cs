using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Organizations;

public interface IInvitationGateway
{
    Task<TerraBaseMember> AcceptAsync(SessionRecord session, string token, CancellationToken cancellationToken);
}

public sealed class TsInvitationGateway(IHttpClientFactory http) : IInvitationGateway
{
    public Task<TerraBaseMember> AcceptAsync(SessionRecord session, string token, CancellationToken cancellationToken)
    {
        var client = http.CreateClient("platform");
        return new TerraBaseManagementClient(client, new()
        {
            EndpointResolver = new TerraBaseDiscovery(),
            AccessTokenProvider = _ => ValueTask.FromResult(session.AccessToken)
        }).AcceptInvitationAsync(session.UserId, token, cancellationToken);
    }
}
