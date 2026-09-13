// <copyright file="TypedApi.Health.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using TerraBase.Protocol;

public sealed partial class TypedApi
{
    // Health
    // ------------------------------------------------------------------

    internal async Task<IResult> HealthAsync(CancellationToken cancellationToken)
    {
        var health = await this.supervisor.HealthAsync(cancellationToken).ConfigureAwait(false);
        var status = health.Status == EngineStatus.Serving && health.EngineState is not ("Stopped" or "Draining")
            ? "serving"
            : "unavailable";
        return Results.Json(
            new HealthResponse(
                Status: status,
                Engine: new EngineHealth(
                    health.EngineState,
                    health.Sessions,
                    health.Subscriptions,
                    health.FramesServed,
                    health.Generation,
                    health.Restarts,
                    PublicHealthDetail(health))),
            statusCode: status == "serving" ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }

    // ------------------------------------------------------------------

}
