using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Auth;

public partial interface ITsAuthProjects
{
    Task<TsAuthImpersonationTicket> StartImpersonationAsync(SessionRecord session, string projectId, string userId, TsAuthImpersonationRequest request, string proof, CancellationToken cancellationToken);
    Task<TsAuthImpersonations> ListImpersonationsAsync(SessionRecord session, string projectId, CancellationToken cancellationToken);
    Task EndImpersonationAsync(SessionRecord session, string projectId, string id, CancellationToken cancellationToken);
}

public sealed partial class TsAuthGateway
{
    public Task<TsAuthImpersonationTicket> StartImpersonationAsync(SessionRecord session, string projectId, string userId, TsAuthImpersonationRequest request, string proof, CancellationToken cancellationToken) =>
        ProjectAsync(() => Client().Projects.StartImpersonationAsync(Bearer(session), Id(projectId), Id(userId), request, proof, cancellationToken));

    public Task<TsAuthImpersonations> ListImpersonationsAsync(SessionRecord session, string projectId, CancellationToken cancellationToken) =>
        ProjectAsync(() => Client().Projects.ListImpersonationsAsync(Bearer(session), Id(projectId), cancellationToken));

    public Task EndImpersonationAsync(SessionRecord session, string projectId, string id, CancellationToken cancellationToken) =>
        ProjectAsync(async () =>
        {
            await Client().Projects.EndImpersonationAsync(Bearer(session), Id(projectId), Id(id), cancellationToken);
            return true;
        });
}
