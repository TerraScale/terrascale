using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Data;

public sealed record HistoryPageModel(DocumentHistoryState History) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static HistoryPageModel From(DashboardStore store) => new(store.DocumentHistory)
    {
        Shell = ShellFactory.Create(store, "/history"),
    };
}

public sealed class HistoryPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<HistorySlice, HistoryPageModel>(store, "/history")
{
    protected override HistoryPageModel Load() => HistoryPageModel.From(Store);
}
