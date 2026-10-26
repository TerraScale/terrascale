using System.Net;

namespace TerraScale.DotNet.TerraBase;

/// <summary>A public probe's status body.</summary>
/// <param name="Status">ok, ready, shipping or unavailable.</param>
/// <param name="Version">Service version.</param>
/// <param name="Region">Answering region.</param>
/// <param name="StorageFormat">On-disk format version.</param>
/// <param name="Revision">Build source revision, or null.</param>
/// <param name="Machine">Answering machine.</param>
public sealed record TerraBaseStatus(string Status, string Version, string Region, string StorageFormat, string? Revision, string Machine);

/// <summary>A probe preserves both available (200) and unavailable (503) status bodies.</summary>
/// <param name="StatusCode">HTTP status.</param>
/// <param name="Body">Probe details.</param>
public sealed record TerraBaseProbeResult(HttpStatusCode StatusCode, TerraBaseStatus Body);

/// <summary>A peer region's replication health.</summary>
/// <param name="Region">Peer region.</param>
/// <param name="Status">ok or degraded.</param>
/// <param name="LagSeconds">Oldest unreplicated change age, or null without an observation.</param>
public sealed record TerraBaseReplicationPeerHealth(string Region, string Status, double? LagSeconds);

/// <summary>The node's public replication summary; HTTP 200 can still carry degraded status.</summary>
/// <param name="Region">Answering region.</param>
/// <param name="Status">ok or degraded.</param>
/// <param name="MaxLagSeconds">Maximum observed lag, or null.</param>
/// <param name="Peers">Peer regions in region order.</param>
public sealed record TerraBaseReplicationHealth(string Region, string Status, double? MaxLagSeconds, IReadOnlyList<TerraBaseReplicationPeerHealth> Peers);

/// <summary>Anonymous public TerraBase health probes over a caller-owned HttpClient. Thread-safe; sends no credentials.</summary>
public sealed class TerraBaseHealthClient
{
    private readonly TerraBaseConnection connection;
    private static readonly TerraBaseJsonContext Json = TerraBaseJsonContext.Wire;

    /// <summary>Creates a probe client. Use TerraBaseHttp.CreateHandler to disable redirects and cookies; optional region pins the answering region.
    /// Default Authorization or Cookie headers are rejected before dispatch; use a separate credential-free HttpClient.</summary>
    public TerraBaseHealthClient(HttpClient httpClient, Uri? baseAddress = null, string? region = null) =>
        connection = new TerraBaseConnection(httpClient, baseAddress, null, null, "credential", TimeSpan.FromSeconds(30),
            TerraBaseConnection.DefaultMaxResponseBytes, region: region, publicAnonymous: true);

    /// <summary>Reads process liveness without credentials.</summary>
    public Task<TerraBaseProbeResult> GetHealthAsync(CancellationToken cancellationToken = default) => ProbeAsync("/healthz", cancellationToken);

    /// <summary>Reads readiness, preserving the 503 unavailable body.</summary>
    public Task<TerraBaseProbeResult> GetReadinessAsync(CancellationToken cancellationToken = default) => ProbeAsync("/readyz", cancellationToken);

    /// <summary>Reads backup readiness, preserving the 503 unavailable body.</summary>
    public Task<TerraBaseProbeResult> GetBackupHealthAsync(CancellationToken cancellationToken = default) => ProbeAsync("/backupz", cancellationToken);

    /// <summary>Reads replication health; inspect Status even when HTTP succeeds.</summary>
    public async Task<TerraBaseReplicationHealth> GetReplicationHealthAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, "/health/replication", null, Json.TerraBaseReplicationHealth, null, cancellationToken).ConfigureAwait(false)).Value;

    private Task<TerraBaseProbeResult> ProbeAsync(string path, CancellationToken cancellationToken) =>
        connection.ExchangeAsync(path, null, null, async opened =>
        {
            var response = opened.Response;
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable))
                throw await TerraBaseException.FromResponseAsync(response, opened.Token).ConfigureAwait(false);
            var bytes = await connection.ReadBodyAsync(response, opened.Token).ConfigureAwait(false);
            return new TerraBaseProbeResult(response.StatusCode, TerraBaseConnection.ReadResponse(bytes, Json.TerraBaseStatus, response.StatusCode));
        }, cancellationToken);
}
