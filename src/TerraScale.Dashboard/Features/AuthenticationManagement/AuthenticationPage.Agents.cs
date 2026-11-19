using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.AuthenticationManagement;

public sealed partial record AuthenticationPage
{
    public IReadOnlyList<TsAuthAgent>? Agents { get; init; }
    public TsAuthAgent? SelectedAgent { get; init; }
    public TsAuthAgentPolicy? AgentPolicy { get; init; }
    public string AgentId { get; init; } = "";
    public string AgentOwner { get; init; } = "";
}
