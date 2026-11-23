using System.Net;

namespace TerraScale.DotNet.TerraBase;

public sealed record TerraBaseStatus(string Status, string Version, string Region);

public sealed record TerraBaseProbeResult(HttpStatusCode StatusCode, TerraBaseStatus Body);

public sealed class TerraBaseHealthClient
{
    private readonly TerraBaseConnection connection;
    private static readonly TerraBaseJsonContext Json = TerraBaseJsonContext.Wire;

    public TerraBaseHealthClient(HttpClient httpClient, Uri? baseAddress = null, string? region = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        connection = new TerraBaseConnection(httpClient, TerraBaseRegion.OriginOrPublic(baseAddress ?? httpClient.BaseAddress, null), null, null, "credential",
            TerraBaseConnection.RequestDeadline, TerraBaseConnection.DefaultMaxResponseBytes, region: region, publicAnonymous: true);
    }

    public Task<TerraBaseProbeResult> GetHealthAsync(CancellationToken cancellationToken = default) => ProbeAsync("/healthz", cancellationToken);

    public Task<TerraBaseProbeResult> GetReadinessAsync(CancellationToken cancellationToken = default) => ProbeAsync("/readyz", cancellationToken);

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
