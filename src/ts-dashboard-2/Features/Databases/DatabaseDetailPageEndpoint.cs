using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Databases;

public sealed class DatabaseDetailPageEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/databases/{databaseId}");
        Summary("Renders the database detail page");
        Tags("Pages");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var databaseId = HttpContext.Request.RouteValues["databaseId"] as string ?? "";
        var model = DatabaseDetailPageModel.From(
            store,
            databaseId,
            HttpContext.Request.Query["branched"].FirstOrDefault() == "1");
        return Task.FromResult<IResult>(model is null
            ? Results.NotFound()
            : DatabaseDetailSlice.Create(model));
    }
}
