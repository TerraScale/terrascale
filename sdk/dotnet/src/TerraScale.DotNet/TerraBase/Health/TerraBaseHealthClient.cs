using System.Net;

namespace TerraScale.DotNet.TerraBase;

/// <summary>A public probe's three required, non-null status fields. Private build/node metadata is not part of this DTO.</summary>
/// <param name="Status">ok, ready, shipping or unavailable.</param>
/// <param name="Version">Service version.</param>
/// <param name="Region">Answering region.</param>
public sealed record TerraBaseStatus(string Status, string Version, string Region);

/// <summary>A probe preserves both available (200) and unavailable (503) status bodies.</summary>
/// <param name="StatusCode">HTTP status.</param>
/// <param name="Body">Probe details.</param>
public sealed record TerraBaseProbeResult(HttpStatusCode StatusCode, TerraBaseStatus Body);

/// <summary>Anonymous public TerraBase health probes over a caller-owned HttpClient. Thread-safe; sends no credentials.</summary>
public sealed class TerraBaseHealthClient
{
    private readonly TerraBaseConnection connection;
    private static readonly TerraBaseJsonContext Json = TerraBaseJsonContext.Wire;

    /// <summary>Creates a probe client for <paramref name="baseAddress"/>, else the HttpClient's BaseAddress, else <c>https://api.terrascale.tech</c>.
    /// Use TerraBaseHttp.CreateHandler to disable redirects and cookies; optional region pins the answering region.
    /// Default Authorization or Cookie headers are rejected before dispatch; use a separate credential-free HttpClient.</summary>
    public TerraBaseHealthClient(HttpClient httpClient, Uri? baseAddress = null, string? region = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        connection = new TerraBaseConnection(httpClient, TerraBaseRegion.OriginOrPublic(baseAddress ?? httpClient.BaseAddress, null), null, null, "credential",
            TerraBaseConnection.RequestDeadline, TerraBaseConnection.DefaultMaxResponseBytes, region: region, publicAnonymous: true);
    }

    /// <summary>Reads process liveness without credentials.</summary>
    public Task<TerraBaseProbeResult> GetHealthAsync(CancellationToken cancellationToken = default) => ProbeAsync("/healthz", cancellationToken);

    /// <summary>Reads readiness, preserving the 503 unavailable body.</summary>
    public Task<TerraBaseProbeResult> GetReadinessAsync(CancellationToken cancellationToken = default) => ProbeAsync("/readyz", cancellationToken);

    /// <summary>Reads backup readiness, preserving the 503 unavailable body.</summary>
    public Task<TerraBaseProbeResult> GetBackupHealthAsync(CancellationToken cancellationToken = default) => ProbeAsync("/backupz", cancellationToken);

    private Task<TerraBaseProbeResult> ProbeAsync(string path, CancellationToken cancellationToken) =>
        connection.ExchangeAsync(path, null, null, async opened =>
        {
            var response = opened.Response;
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable))
                throw await TerraBaseException.FromResponseAsync(response, null, opened.Token).ConfigureAwait(false);
            var bytes = await connection.ReadBodyAsync(response, opened.Token).ConfigureAwait(false);
            return new TerraBaseProbeResult(response.StatusCode, TerraBaseConnection.ReadResponse(bytes, Json.TerraBaseStatus, response.StatusCode));
        }, cancellationToken);
}
