using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Admin;

public sealed record AdminDatabasesPageModel(
    string Query,
    IReadOnlyList<AdminDatabaseRow> Rows,
    int RegionCount) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}
