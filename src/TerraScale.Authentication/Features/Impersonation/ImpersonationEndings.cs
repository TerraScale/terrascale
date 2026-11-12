using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Webhooks;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Impersonation;

internal static class ImpersonationEndings
{
    internal static IReadOnlyList<Change> Events(ImpersonationRecord record, string reason, string endedBy, WebhookOutbox outbox, TimeProvider clock) =>
    [
        Change.Insert(new AuditEventRecord(AuthIds.New(), OidcIssuer.InstanceId, record.OrganizationId, endedBy, "impersonation.ended", record.UserId,
            $"impersonation={record.Id}; actor={record.ActorId}; reason={record.Reason}; end_reason={reason}", clock.GetUtcNow())),
        Change.Insert(outbox.NewEvent(record.InstanceId, "impersonation.ended", record.UserId, sessionId: record.Id) with
        {
            ActorId = record.ActorId,
            Reason = reason,
        }),
    ];
}
