// <copyright file="TypedApi.Engine.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using TerraBase.Protocol;

public sealed partial class TypedApi
{
    internal async Task<IResult> EngineHealthAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (this.Gate(context, "observer", "operator") is { } refused)
        {
            return refused;
        }

        var health = await this.supervisor.HealthAsync(cancellationToken).ConfigureAwait(false);
        return Results.Json(PublicHealth(health));
    }

    internal async Task<IResult> EngineStatusAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (this.Gate(context, "observer", "operator") is { } refused)
        {
            return refused;
        }

        try
        {
            var status = await this.supervisor
                .CallAsync(
                    context.Request.Headers["X-TB-Tenant"].ToString(),
                    context.Request.Headers["X-TB-Principal"].ToString(),
                    "admin.status",
                    channel => channel.AdminStatusAsync(cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(status);
        }
        catch (Exception failure)
        {
            return this.Failure(failure);
        }
    }

    internal async Task<IResult> EngineTopologyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (this.Gate(context, "observer", "operator") is { } refused)
        {
            return refused;
        }

        try
        {
            var topology = await this.supervisor
                .CallAsync(
                    context.Request.Headers["X-TB-Tenant"].ToString(),
                    context.Request.Headers["X-TB-Principal"].ToString(),
                    "topology.read",
                    channel => channel.TopologyReadAsync(cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(topology);
        }
        catch (Exception failure)
        {
            return this.Failure(failure);
        }
    }

    internal async Task<IResult> EngineShutdownAsync(HttpContext context, ShutdownRequestDto request, CancellationToken cancellationToken)
    {
        if (this.Gate(context, "operator", "supervisor") is { } refused)
        {
            return refused;
        }

        var requiredRole = request.Reason == "supervisor" ? "supervisor" : "operator";
        if (this.Gate(context, requiredRole) is { } roleRefused)
        {
            return roleRefused;
        }

        var caller = ResolveCaller(context);
        try
        {
            var reason = request.Reason == "supervisor" ? ShutdownReason.Supervisor() : ShutdownReason.Operator();
            var answer = await this.supervisor.ShutdownAsync(reason, cancellationToken).ConfigureAwait(false);
            return Results.Json(new ShutdownResponseDto(answer.State.Variant, answer.InFlight));
        }
        catch (EngineUnavailableException failure)
        {
            return Unavailable(failure);
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }
}
