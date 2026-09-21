namespace TerraScale.Dashboard.Features.Access;

public sealed record AccessProjectView(
    string Id,
    string Name,
    bool IsCurrent,
    string CreatedAtText);
