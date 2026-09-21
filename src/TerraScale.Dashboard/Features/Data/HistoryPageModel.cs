using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Data;

public sealed record HistoryPageModel(DocumentHistoryState History) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static HistoryPageModel From(DashboardStore store, string? message = null) =>
        new(store.DocumentHistory with { Message = message ?? store.DocumentHistory.Message })
    {
        Shell = ShellFactory.Create(store, "/history"),
    };
}
