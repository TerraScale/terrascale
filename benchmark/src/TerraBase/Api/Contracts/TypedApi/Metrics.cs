// <copyright file="TypedApi.Metrics.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

public sealed partial class TypedApi
{
    internal IResult Metrics(HttpContext context)
    {
        if (this.Gate(context, "observer", "operator") is { } refused)
        {
            return refused;
        }

        return Results.Json(new MetricsResponse(
            EngineGeneration: this.supervisor.Generation,
            EngineRestarts: this.supervisor.Restarts,
            EngineStatus: this.supervisor.Status.ToString().ToLowerInvariant(),
            OpenStreams: this.hub.OpenCount,
            DroppedFrames: this.hub.DroppedTotal,
            TrackedSessions: this.supervisor.Sessions.Count,
            AuditRecords: this.audit.Count));
    }
}
