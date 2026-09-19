using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Backups;

public sealed class BackupsPageEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/backups");
        Summary("Renders the Backups page");
        Tags("Pages");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct) =>
        Task.FromResult<IResult>(BackupsSlice.Create(BackupsPageModel.From(
            store,
            HttpContext.Request.Query["plan"].FirstOrDefault(),
            HttpContext.Request.Query["restored"].FirstOrDefault(),
            HttpContext.Request.Query["error"].FirstOrDefault())));
}
