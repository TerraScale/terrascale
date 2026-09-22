namespace TerraScale.Dashboard.Features.Admin.ViewModels;

public sealed record AdminDatabaseRow(
    string DatabaseId,
    string Name,
    IReadOnlyList<string> Regions,
    string Version,
    string CreatedAt);
