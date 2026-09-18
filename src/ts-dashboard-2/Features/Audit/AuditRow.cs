namespace TerraScale.Dashboard2.Features.Audit;

public sealed record AuditRow(
    string Id,
    string Time,
    string TimeLabel,
    string Actor,
    string ActorType,
    string Action,
    string ActionIcon,
    string Target,
    string Source,
    string Severity,
    string SeverityLabel,
    string SeverityTone,
    bool Failure);
