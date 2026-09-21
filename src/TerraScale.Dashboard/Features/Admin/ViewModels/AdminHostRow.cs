namespace TerraScale.Dashboard.Features.Admin.ViewModels;

public sealed record AdminHostRow(
    string DatabaseId,
    string Name,
    IReadOnlyList<string> Regions,
    string Version,
    string CreatedAt);
