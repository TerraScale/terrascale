using System.Net;
using System.Text.Json.Nodes;

namespace TerraBase.E2E;

/// <summary>
/// A shape client of protocol v1 (docs/terrabase/shape-reads.md sections 3, 7 and 8) holding a materialized view of one
/// collection through one region: <c>offset=-1</c> (307 to the newest snapshot), snapshot pages, then chunks until a fresh
/// response says <c>up_to_date</c>. A <c>409 must_refetch</c> discards the view and restarts at <c>-1</c> (it never
/// resumes: FR-23); <c>429</c> and <c>503</c> keep the view and retry; <c>404 unknown_collection</c> is an empty view.
/// </summary>
internal sealed class ShapeReader(HttpClient client, string collectionPath)
{
    private readonly SortedDictionary<string, string> _view = [with(StringComparer.Ordinal)];
    private string? _handle;
    private string _offset = "-1";

    /// <summary>How many times the server sent this reader back to <c>-1</c>.</summary>
    public int Refetches { get; private set; }

    /// <summary>Responses applied (200s).</summary>
    public int Responses { get; private set; }

    /// <summary>The view as a listing, comparable with <see cref="Docs.ListAsync"/>.</summary>
    public Listing View => new(_view.Count, Docs.Digest(_view.Select(document => (document.Key, document.Value))));

    /// <summary>Reads until a response is up to date; false when the shape kept answering retry-later.</summary>
    public async Task<bool> CatchUpAsync(CancellationToken token = default)
    {
        for (var step = 0; step < 500; step++)
        {
            if (await StepAsync(token))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> StepAsync(CancellationToken token)
    {
        var query = _offset == "-1" ? "offset=-1" : $"handle={Uri.EscapeDataString(_handle!)}&offset={Uri.EscapeDataString(_offset)}";
        using var response = await GetAsync(new Uri($"{collectionPath}/shape?{query}", UriKind.Relative), token);
        var text = await response.Content.ReadAsStringAsync(token);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Conflict when text.Contains("\"must_refetch\"", StringComparison.Ordinal):
                Restart();
                Refetches++;
                return false;
            case HttpStatusCode.NotFound when text.Contains("\"unknown_collection\"", StringComparison.Ordinal):
                Restart();
                return true;
            case HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable:
                await Task.Delay(TimeSpan.FromMilliseconds(250), token);
                return false;
            case HttpStatusCode.OK:
                break;
            default:
                await Docs.EnsureSuccessAsync(response);
                throw new HttpRequestException($"shape answered {(int)response.StatusCode}: {text}");
        }

        var body = JsonNode.Parse(text)!;
        if (_offset == "-1")
        {
            // A bootstrap without a resume cursor starts from empty state (rule 3).
            _view.Clear();
        }

        foreach (var change in body["changes"]!.AsArray())
        {
            foreach (var op in change!["ops"]!.AsArray())
            {
                var id = op!["id"]!.GetValue<string>();
                if (op["op"]!.GetValue<string>() == "upsert")
                {
                    _view[id] = Docs.Canonical(op["document"]);
                }
                else
                {
                    _ = _view.Remove(id);
                }
            }
        }

        (_handle, _offset) = (body["handle"]!.GetValue<string>(), body["next_offset"]!.GetValue<string>());
        Responses++;
        return body["up_to_date"]!.GetValue<bool>();
    }

    private void Restart()
    {
        _view.Clear();
        (_handle, _offset) = (null, "-1");
    }

    // The bootstrap's 307 stays on the same origin. HttpClient follows it but drops Authorization (the answer is then a 401
    // from the redirect target); a client that does not follow returns the 307. Either way the target is sent again.
    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken token)
    {
        var response = await client.GetAsync(uri, token);
        var target = response.StatusCode switch
        {
            HttpStatusCode.TemporaryRedirect => response.Headers.Location,
            HttpStatusCode.Unauthorized when response.RequestMessage?.RequestUri is { } final && final.Query.Contains("handle=", StringComparison.Ordinal)
                && !uri.OriginalString.Contains("handle=", StringComparison.Ordinal) => final,
            _ => null,
        };
        if (target is null)
        {
            return response;
        }

        response.Dispose();
        return await client.GetAsync(new Uri(target.IsAbsoluteUri ? target.PathAndQuery : target.OriginalString, UriKind.Relative), token);
    }
}
