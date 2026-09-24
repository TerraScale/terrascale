using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Admin.Pages.Databases;

public sealed record AdminDatabasesPageModel(
    string Query,
    IReadOnlyList<AdminDatabaseRow> Rows,
    int RegionCount,
    int Page,
    int TotalPages,
    int TotalRows) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}
