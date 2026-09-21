using System.Text.Json;
using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Admin;

/// <summary>Port of ts-dashboard's e2e dev shim: promotes the signed-in
/// account to a platform-owner session so the admin pages render, the way
/// POST /api/v1/testing/dev-shim/reset { promoteOwner: true } does.
/// Production-only deployment: the shim 404s unless TS2_E2E=1 (set by the
/// Playwright webServer) so a deployed instance can never hand out owner.</summary>
public sealed class DevShimResetEndpoint(DashboardStore store, IConfiguration configuration) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/testing/dev-shim/reset");
        Summary("E2E dev shim: promotes the session to owner");
        Tags("Admin");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        if (configuration["TS2_E2E"] != "1")
        {
            return Results.NotFound();
        }

        using var document = await JsonDocument.ParseAsync(HttpContext.Request.Body, cancellationToken: ct);
        if (document.RootElement.TryGetProperty("promoteOwner", out var promote) && promote.GetBoolean())
        {
            store.PromoteSessionToOwner();
        }

        return Results.Ok(new { ok = true });
    }
}
