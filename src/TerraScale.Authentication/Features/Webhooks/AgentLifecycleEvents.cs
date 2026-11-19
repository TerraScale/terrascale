using TerraScale.Authentication.Features.Webhooks;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Agents;

public sealed class AgentLifecycleEvents(WebhookOutbox outbox)
{
    public Change Created(string project, string ownerUserId, string agentId, string kid) => this.Event(project, "agent.created", ownerUserId, agentId, kid);

    public Change Revoked(string project, string ownerUserId, string agentId) => this.Event(project, "agent.revoked", ownerUserId, agentId, null);

    public Change KeyAdded(string project, string ownerUserId, string agentId, string kid) => this.Event(project, "agent.key_added", ownerUserId, agentId, kid);

    public Change KeyRevoked(string project, string ownerUserId, string agentId, string kid) => this.Event(project, "agent.key_revoked", ownerUserId, agentId, kid);

    private Change Event(string project, string type, string ownerUserId, string agentId, string? kid) =>
        Change.Insert(outbox.NewEvent(project, type, ownerUserId, agentId: agentId, agentKeyId: kid));
}
