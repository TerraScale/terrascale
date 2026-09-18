using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Auth;

public sealed class OAuthConsentSubmitEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/oauth/consent/submit");
        Summary("Records the OAuth consent decision");
        Tags("Auth");
        AllowAnonymous();
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var outcome = form["action"].ToString() == "approve" ? "approved" : "denied";
        return Results.Redirect($"/oauth/consent?client_id={Uri.EscapeDataString(form["client_id"].ToString())}&outcome={outcome}", false, false);
    }
}
