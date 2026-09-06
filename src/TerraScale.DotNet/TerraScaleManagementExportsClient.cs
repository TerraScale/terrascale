using System.Globalization;
using TerraScale.DotNet.Generated;

#pragma warning disable CS1591
namespace TerraScale.DotNet;

/// <summary>Durable export jobs and streaming artifact downloads.</summary>
public sealed class TerraScaleManagementExportsClient(TerraScaleClient client)
{
    public Task<ExportAccepted> CreateAsync(string database, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        return client.SendJsonAsync(HttpMethod.Post, Route(database), new CreateExport(idempotencyKey),
            TerraScaleContractJsonContext.Default.CreateExport, TerraScaleContractJsonContext.Default.ExportAccepted, cancellationToken, idempotencyKey);
    }

    public Task<ExportList> ListAsync(string database, int limit = 50, string? after = null, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(limit));
        var route = Route(database) + "?limit=" + limit.ToString(CultureInfo.InvariantCulture);
        if (after is not null) route += "&after=" + TerraScaleManagementPaths.Escape(after);
        return client.SendJsonAsync(HttpMethod.Get, route, TerraScaleContractJsonContext.Default.ExportList, cancellationToken);
    }

    public Task<ExportStatus> GetAsync(string database, string jobId, CancellationToken cancellationToken = default) =>
        client.SendJsonAsync(HttpMethod.Get, Job(database, jobId), TerraScaleContractJsonContext.Default.ExportStatus, cancellationToken);

    public Task<ExportStatus> ResumeAsync(string database, string jobId, string resumeCursor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resumeCursor);
        return client.SendNonRetryableJsonAsync(HttpMethod.Post, Job(database, jobId) + "/resume", new ResumeExport(resumeCursor),
            TerraScaleContractJsonContext.Default.ResumeExport, TerraScaleContractJsonContext.Default.ExportStatus, cancellationToken);
    }

    public Task<ExportStatus> CancelAsync(string database, string jobId, CancellationToken cancellationToken = default) =>
        client.SendJsonAsync(HttpMethod.Post, Job(database, jobId) + "/cancel", TerraScaleContractJsonContext.Default.ExportStatus, cancellationToken, idempotencyKey: string.Empty);

    /// <summary>Copies one immutable artifact to the caller-owned destination without buffering the complete export.</summary>
    public Task DownloadArtifactAsync(string database, string jobId, string member, Stream destination, CancellationToken cancellationToken = default) =>
        client.DownloadExportArtifactAsync(Job(database, jobId) + "/artifacts/" + TerraScaleManagementPaths.Escape(member), destination, cancellationToken);

    private static string Route(string database) => TerraScaleManagementPaths.Database(database) + "/exports";
    private static string Job(string database, string jobId) => Route(database) + "/" + TerraScaleManagementPaths.Escape(jobId);
}
