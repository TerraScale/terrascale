using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Databases;

public sealed class RequestBranchEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/databases/branch");
        Summary("Requests a preview branch for a database");
        Tags("Databases");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var databaseId = HttpContext.Request.Form["databaseId"].ToString();
        return Task.FromResult<IResult>(Results.Redirect("/databases/" + databaseId + "?branched=1", false, false));
    }
}
