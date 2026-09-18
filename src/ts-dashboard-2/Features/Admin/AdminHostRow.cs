namespace TerraScale.Dashboard2.Features.Admin;

public sealed record AdminHostRow(
    string DatabaseId,
    string Name,
    IReadOnlyList<string> Regions,
    string Version,
    string CreatedAt);
