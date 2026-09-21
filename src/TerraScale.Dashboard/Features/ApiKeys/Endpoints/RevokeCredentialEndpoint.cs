using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.ApiKeys.Endpoints;

public sealed class RevokeCredentialEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/credentials/revoke");
        Summary("Revokes an API key or M2M client permanently");
        Tags("ApiKeys");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var kind = form["kind"].ToString();
        var id = form["id"].ToString();
        var name = form["name"].ToString();

        if (kind == "m2m")
        {
            store.RevokeM2mCredential(id);
        }
        else
        {
            store.RevokeApiKey(id);
        }

        return Results.Redirect($"/api-keys?toast={Uri.EscapeDataString($"{name} revoked.")}", false, false);
    }
}
