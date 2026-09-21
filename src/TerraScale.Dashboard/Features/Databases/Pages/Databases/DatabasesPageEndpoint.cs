using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Databases.Pages.Databases;

public sealed class DatabasesPageEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/databases");
        Summary("Renders the Databases page");
        Tags("Pages");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct) =>
        Task.FromResult<IResult>(DatabasesSlice.Create(DatabasesPageModel.From(
            store,
            HttpContext.Request.Query["q"].FirstOrDefault(),
            HttpContext.Request.Query["create"].FirstOrDefault() == "1",
            HttpContext.Request.Query["error"].FirstOrDefault() ?? store.DatabaseListError)));
}
