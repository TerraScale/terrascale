using System.Diagnostics.Metrics;
using TerraBase.Hosting;

namespace TerraBase.Features.Callers;

internal sealed record AdmittedCaller(CallerIdentity? Identity);

internal sealed class CallerAdmission(CallerTokens tokens, InternalCredential peer, IMeterFactory meters)
{
    private readonly Counter<long> _requests = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.internal.authentication.requests");

    public async Task<bool> AdmitAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (await tokens.ReadAsync(context.Request) is { } identity)
        {
            Count("caller", identity.Key.Caller);
            context.Features.Set(new AdmittedCaller(identity));
            return true;
        }

        if (context.Request.Headers.ContainsKey(CallerTokens.Header))
        {
            Count("caller_rejected", "peer");
            return false;
        }

        if (!peer.Matches(context))
        {
            Count("unmatched", "peer");
            return false;
        }

        Count("peer", "peer");
        context.Features.Set(new AdmittedCaller(null));
        return true;
    }

    private void Count(string credential, string caller) =>
        _requests.Add(1, new KeyValuePair<string, object?>("credential", credential), new KeyValuePair<string, object?>("caller", caller));
}
