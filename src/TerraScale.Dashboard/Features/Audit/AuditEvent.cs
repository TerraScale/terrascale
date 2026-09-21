namespace TerraScale.Dashboard;

public sealed record AuditEvent(
    string Id,
    string Actor,
    string Action,
    string Target,
    string TargetType,
    string Risk,            // low | medium | high
    string Source,
    string OccurredAt);
