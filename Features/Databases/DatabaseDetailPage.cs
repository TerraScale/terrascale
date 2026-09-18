using RazorSlices;
using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Databases;

public sealed record DatabaseDetailPageModel : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public string DatabaseId { get; init; } = "";
}

public sealed class DatabaseDetailPageEndpoint(DashboardStore store) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/databases/{databaseId}");
        Summary("Renders the DatabaseDetail page");
        Tags("Pages");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var model = new DatabaseDetailPageModel
        {
            Shell = ShellFactory.Create(store, "/databases"),
            DatabaseId = HttpContext.Request.RouteValues["databaseId"] as string ?? "",
        };
        return Task.FromResult<IResult>(DatabaseDetailSlice.Create(model));
    }
}
