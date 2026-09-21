namespace TerraScale.Dashboard.Features.Access.ViewModels;

public sealed record AccessProjectView(
    string Id,
    string Name,
    bool IsCurrent,
    string CreatedAtText);
