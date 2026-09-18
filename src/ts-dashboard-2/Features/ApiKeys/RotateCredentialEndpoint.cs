using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.ApiKeys;

public sealed class RotateCredentialEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/credentials/rotate");
        Summary("Rotates an API key or M2M client secret");
        Tags("ApiKeys");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var kind = form["kind"].ToString();
        var id = form["id"].ToString();
        var name = form["name"].ToString();

        var secret = kind == "m2m" ? store.RotateM2mCredential(id) : store.RotateApiKey(id);
        store.SavePendingSecret(id, name, secret);
        return Results.Redirect($"/api-keys?rotated=1&toast={Uri.EscapeDataString("Credential rotated.")}", false, false);
    }
}
