using System.Net;
using System.Net.Http.Headers;

namespace TerraScale.DotNet.TerraBase;

/// <summary>A project-scoped Prometheus 0.0.4 exposition, without JSON decoding or text rewriting.</summary>
/// <param name="ContentType">The response media type, including version and charset.</param>
/// <param name="Body">The original exposition bytes. Best-effort observations, not billing.</param>
public sealed record TerraBasePrometheusMetrics(string ContentType, ReadOnlyMemory<byte> Body);

public sealed partial class TerraBaseProjectClient
{
    private const int MaxPrometheusBytes = 8 * 1024 * 1024;
    private const string PrometheusContentType = "text/plain; version=0.0.4; charset=utf-8";

    /// <summary>
    /// Reads GET project/metrics with a project access token holding databases:read, respecting database narrowing.
    /// Preserves Prometheus exposition bytes and media type. Rates cover the last five complete UTC minutes;
    /// observations are best effort, not billing. Sent once, with an 8 MiB body cap and 30-second overall deadline;
    /// earlier client limits and caller cancellation still apply. No pagination, retry or background scraping.
    /// </summary>
    public async Task<TerraBasePrometheusMetrics> GetPrometheusMetricsAsync(CancellationToken cancellationToken = default)
    {
        using var expiry = new CancellationTokenSource(TimeSpan.FromSeconds(30), connection.TimeProvider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
        try
        {
            return await connection.ExchangeAsync(path + "/metrics", null, headers =>
            {
                headers.Accept.Clear();
                headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(PrometheusContentType));
            }, async opened =>
            {
                var response = opened.Response;
                if (!response.IsSuccessStatusCode)
                    throw await TerraBaseException.FromResponseAsync(response, opened.Token).ConfigureAwait(false);
                var media = response.Content.Headers.ContentType;
                if (response.StatusCode != HttpStatusCode.OK || media is null
                    || !string.Equals(media.MediaType, "text/plain", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(media.CharSet?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase)
                    || media.Parameters.Count(p => p.Name.Equals("version", StringComparison.OrdinalIgnoreCase) && p.Value?.Trim('"') == "0.0.4") != 1)
                    throw InvalidMetrics(response.StatusCode);
                var limit = Math.Min(MaxPrometheusBytes, connection.MaxResponseBytes);
                if (response.Content.Headers.ContentLength > limit) throw InvalidMetrics(response.StatusCode);
                using var body = new BoundedHttpBodyStream(limit, () => InvalidMetrics(response.StatusCode), opened.Token);
                await response.Content.CopyToAsync(body, opened.Token).ConfigureAwait(false);
                return new TerraBasePrometheusMetrics(media.ToString(), body.ToArray());
            }, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && expiry.IsCancellationRequested)
        {
            throw new TimeoutException("TerraBase metrics exceeded the 30-second request deadline.", error);
        }
        finally
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static TerraBaseException InvalidMetrics(HttpStatusCode status) =>
        new(status, "TerraBase returned an unexpected metrics status, media type or oversized exposition.", "invalid_response", null);
}
