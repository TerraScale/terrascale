using System.Net;
using System.Net.Http.Headers;

namespace ApiBench;

/// <summary>Observes each HTTP send without changing SDK retries, headers, content or protocol negotiation.</summary>
internal sealed class ParityAttempts(HttpMessageHandler inner, Uri target, string region, Version? requiredProtocol) : DelegatingHandler(inner)
{
    private long attempts, ok, h1, h2, wrongTarget, wrongPin, wrongRegion, wrongProtocol;

    // Read only after the caller drained every operation; individual counters are atomic, not a transactional snapshot.
    internal ParityAttemptSnapshot Snapshot => new(Interlocked.Read(ref attempts), Interlocked.Read(ref ok),
        Interlocked.Read(ref h1), Interlocked.Read(ref h2), Interlocked.Read(ref wrongTarget),
        Interlocked.Read(ref wrongPin), Interlocked.Read(ref wrongRegion), Interlocked.Read(ref wrongProtocol));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref attempts);
        if (request.Method != HttpMethod.Get || !string.Equals(request.RequestUri?.AbsoluteUri, target.AbsoluteUri, StringComparison.Ordinal))
            Interlocked.Increment(ref wrongTarget);
        if (!Single(request.Headers, "X-TerraScale-Region-Pin", region))
            Interlocked.Increment(ref wrongPin);

        // Return refusals unchanged so the shipped SDK decides whether to retry or refresh.
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.OK) Interlocked.Increment(ref ok);
        if (!Single(response.Headers, "X-TerraBase-Region", region)) Interlocked.Increment(ref wrongRegion);
        if (response.Version == HttpVersion.Version11) Interlocked.Increment(ref h1);
        if (response.Version == HttpVersion.Version20) Interlocked.Increment(ref h2);
        if ((response.Version != HttpVersion.Version11 && response.Version != HttpVersion.Version20)
            || (requiredProtocol is not null && response.Version != requiredProtocol))
            Interlocked.Increment(ref wrongProtocol);
        return response;
    }

    private static bool Single(HttpHeaders headers, string name, string expected)
    {
        if (!headers.TryGetValues(name, out var values)) return false;
        using var items = values.GetEnumerator();
        return items.MoveNext() && string.Equals(items.Current, expected, StringComparison.Ordinal) && !items.MoveNext();
    }
}

internal readonly record struct ParityAttemptSnapshot(long Attempts, long Ok, long H1, long H2,
    long WrongTarget, long WrongPin, long WrongRegion, long WrongProtocol)
{
    internal ParityAttemptSnapshot Since(ParityAttemptSnapshot before) => new(Attempts - before.Attempts, Ok - before.Ok,
        H1 - before.H1, H2 - before.H2, WrongTarget - before.WrongTarget, WrongPin - before.WrongPin,
        WrongRegion - before.WrongRegion, WrongProtocol - before.WrongProtocol);

    // A cell still needs successful full-body/document validation and a bounded drain; metadata alone cannot qualify it.
    internal string? RejectionSince(ParityAttemptSnapshot before, long logicalStarts)
    {
        if (logicalStarts <= 0) return "empty";
        if (Attempts - before.Attempts != logicalStarts) return "attempts";
        if (WrongTarget != before.WrongTarget) return "target";
        if (WrongPin != before.WrongPin) return "pin";
        if (WrongRegion != before.WrongRegion) return "region";
        if (WrongProtocol != before.WrongProtocol) return "protocol";
        return Ok - before.Ok == logicalStarts ? null : "status";
    }
}
