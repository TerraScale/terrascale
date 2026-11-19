using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.AuthenticationManagement;

public sealed partial record AuthenticationPage
{
    public IReadOnlyList<TsAuthAgent>? Agents { get; init; }
    public TsAuthAgent? SelectedAgent { get; init; }
    public TsAuthAgentPolicy? AgentPolicy { get; init; }
    public string AgentId { get; init; } = "";
    public string AgentOwner { get; init; } = "";
    public QueryString AgentQuery => Query(AgentId, AgentOwner, After);

    internal static QueryString Query(string agent, string owner, string after) =>
        QueryString.Create(new Dictionary<string, string?> { ["agent"] = agent, ["owner"] = owner, ["after"] = after }
            .Where(pair => pair.Value is { Length: > 0 }));
}
