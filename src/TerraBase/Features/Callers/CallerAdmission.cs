using System.Diagnostics.Metrics;
using TerraBase.Features.Management;
using TerraBase.Features.Platform;
using TerraBase.Features.Regions;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;

namespace TerraBase.Features.Callers;

internal sealed class CallerAdmission(CallerTokens tokens, InternalCredential peer, IMeterFactory meters)
{
    private readonly Counter<long> _requests = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.internal.authentication.requests");
    private readonly Counter<long> _legacy = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.operator.legacy_grants");

    internal void Legacy(string kind) => _legacy.Add(1, new KeyValuePair<string, object?>("kind", kind));

    internal async Task<bool> AdmitAsync(HttpContext context, bool forwarded = false)
    {
        var caller = await tokens.ReadAsync(context.Request).ConfigureAwait(false);
        var capability = context.GetEndpoint()?.Metadata.GetMetadata<Requires>();
        var listener = context.GetEndpoint()?.Metadata.GetMetadata<ListenerMetadata>();
        if (caller is null)
        {
            var matches = peer.Matches(context);
            var credential = !matches ? "unmatched" : context.Request.Headers.ContainsKey(CallerTokens.Header) ? "caller_rejected"
                : capability is not null || ReferenceEquals(listener, ListenerMetadata.Client) || ReferenceEquals(listener, ListenerMetadata.Management)
                    || context.Request.Headers.ContainsKey(OperatorHandler.UserHeader) ? "legacy" : "peer";
            Count(credential, "peer");
            if (!matches)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            }
            return matches;
        }
        Count("caller", caller.Key.Caller);
        context.Features.Set(caller);
        var allowed = !forwarded && !context.Request.Headers.ContainsKey(HomeForwarding.ForwardedForHeader) && Allows(context, caller, capability, listener);
        if (!allowed)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
        }
        return allowed;
    }

    private static bool Allows(HttpContext context, CallerIdentity caller, Requires? capability, ListenerMetadata? listener)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<CallerTransport>() is not null)
        {
            return true;
        }
        if (context.Request.Headers.ContainsKey(OperatorHandler.UserHeader)
            && (caller.Key.Kind != "operator" || !caller.Grants.Contains("act-as")))
        {
            return false;
        }
        if ((ReferenceEquals(listener, ListenerMetadata.Client) || ReferenceEquals(listener, ListenerMetadata.Management))
            && context.Request.Headers.Authorization.Count > 0)
        {
            return true;
        }
        if (capability is not null)
        {
            return capability.Grants.Any(caller.Grants.Contains)
                && !context.Request.Headers.ContainsKey(ClosureFanout.ScopeHeader)
                && !context.Request.Headers.ContainsKey(PlanFanout.ScopeHeader);
        }
        if (!ReferenceEquals(listener, ListenerMetadata.Client) && !ReferenceEquals(listener, ListenerMetadata.Management))
        {
            return false;
        }
        if (context.Request.Headers.Authorization.Count > 0)
        {
            return true;
        }
        if (context.Request.Headers.ContainsKey(OperatorHandler.UserHeader))
        {
            return caller.Grants.Contains("act-as");
        }
        return !ReferenceEquals(listener, ListenerMetadata.Management)
            && context.Request.RouteValues["project"] is string project
            && (caller.Grants.Contains("data:" + project) || PlatformDatabases.IsPlatformProject(project)
                && (caller.Grants.Contains("platform.read") || caller.Grants.Contains("platform.write")));
    }

    private void Count(string credential, string caller) => _requests.Add(1,
        new KeyValuePair<string, object?>("credential", credential), new KeyValuePair<string, object?>("caller", caller));
}
