namespace TerraScale.Dashboard2.Features.Access;

public sealed record AccessProjectView(
    string Id,
    string Name,
    bool IsCurrent,
    string CreatedAtText);
