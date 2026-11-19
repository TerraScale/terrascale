using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Auth;

public partial interface ITsAuthProjects
{
    Task<TsAuthPlatformPage<TsAuthAgent>> ListAgentsAsync(SessionRecord session, string projectId, string? owner, string? after, CancellationToken cancellationToken);
    Task<TsAuthAgent> GetAgentAsync(SessionRecord session, string projectId, string agent, CancellationToken cancellationToken);
    Task RevokeAgentAsync(SessionRecord session, string projectId, string agent, CancellationToken cancellationToken);
    Task RevokeAgentKeyAsync(SessionRecord session, string projectId, string agent, string kid, CancellationToken cancellationToken);
    Task<TsAuthAgentPolicy> GetAgentPolicyAsync(SessionRecord session, string projectId, CancellationToken cancellationToken);
    Task<TsAuthAgentPolicy> UpdateAgentPolicyAsync(SessionRecord session, string projectId, TsAuthAgentPolicy policy, CancellationToken cancellationToken);
}

public sealed partial class TsAuthGateway
{
    public Task<TsAuthPlatformPage<TsAuthAgent>> ListAgentsAsync(SessionRecord session, string projectId, string? owner, string? after, CancellationToken cancellationToken) =>
        ProjectAsync(() => Client().Projects.ListAgentsAsync(Bearer(session), Id(projectId), Blank(owner), Blank(after), cancellationToken));

    public Task<TsAuthAgent> GetAgentAsync(SessionRecord session, string projectId, string agent, CancellationToken cancellationToken) =>
        ProjectAsync(() => Client().Projects.GetAgentAsync(Bearer(session), Id(projectId), Id(agent), cancellationToken));

    public Task RevokeAgentAsync(SessionRecord session, string projectId, string agent, CancellationToken cancellationToken) =>
        ProjectAsync(async () => { await Client().Projects.RevokeAgentAsync(Bearer(session), Id(projectId), Id(agent), cancellationToken); return true; });

    public Task RevokeAgentKeyAsync(SessionRecord session, string projectId, string agent, string kid, CancellationToken cancellationToken) =>
        ProjectAsync(async () => { await Client().Projects.RevokeAgentKeyAsync(Bearer(session), Id(projectId), Id(agent), kid, cancellationToken); return true; });

    public Task<TsAuthAgentPolicy> GetAgentPolicyAsync(SessionRecord session, string projectId, CancellationToken cancellationToken) =>
        ProjectAsync(() => Client().Projects.GetAgentPolicyAsync(Bearer(session), Id(projectId), cancellationToken));

    public Task<TsAuthAgentPolicy> UpdateAgentPolicyAsync(SessionRecord session, string projectId, TsAuthAgentPolicy policy, CancellationToken cancellationToken) =>
        ProjectAsync(() => Client().Projects.UpdateAgentPolicyAsync(Bearer(session), Id(projectId), policy, cancellationToken));
}
