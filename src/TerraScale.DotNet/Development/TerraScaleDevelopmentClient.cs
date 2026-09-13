using System.Globalization;
using TerraScale.DotNet.Generated;

#pragma warning disable CS1591
namespace TerraScale.DotNet;

/// <summary>Authorized bounded development faults and safe branch diagnostics.</summary>
public sealed class TerraScaleDevelopmentClient(TerraScaleClient client)
{
    public Task<DevelopmentFaultResponse> SetFaultAsync(string database, DevelopmentFaultRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return client.SendNonRetryableJsonAsync(HttpMethod.Post, Route(database) + "/faults", request,
            TerraScaleContractJsonContext.Default.DevelopmentFaultRequest, TerraScaleContractJsonContext.Default.DevelopmentFaultResponse, cancellationToken);
    }

    public Task<DevelopmentFaultResponse> ClearFaultAsync(string database, CancellationToken cancellationToken = default) =>
        client.SendJsonAsync(HttpMethod.Delete, Route(database) + "/faults", TerraScaleContractJsonContext.Default.DevelopmentFaultResponse, cancellationToken, string.Empty);

    public Task<DevelopmentLogResponse> LogsAsync(string database, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        return client.SendJsonAsync(HttpMethod.Get, Route(database) + "/logs?limit=" + limit.ToString(CultureInfo.InvariantCulture),
            TerraScaleContractJsonContext.Default.DevelopmentLogResponse, cancellationToken);
    }

    private static string Route(string database) => TerraScaleManagementPaths.Database(database) + "/dev";
}
