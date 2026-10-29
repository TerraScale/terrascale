namespace TerraScale.DotNet;

internal static class ResponseRequestId
{
    // Accept exactly one canonical ULID; never retain arbitrary responder text.
    internal static string? Read(HttpResponseMessage? response)
    {
        if (response is null || !response.Headers.TryGetValues("X-Request-ID", out var values)) return null;
        using var iterator = values.GetEnumerator();
        if (!iterator.MoveNext()) return null;
        var value = iterator.Current;
        return TerraScaleIds.IsValid(value) && !iterator.MoveNext() ? value : null;
    }
}
