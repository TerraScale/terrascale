using System.Text.Json;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

internal static class ReplicationPressure
{
    public static async Task<long?> ReadAsync(TerraBaseTestServer server, CancellationToken cancellationToken)
    {
        using var response = await server.Client.GetAsync(new Uri("/admin/node/pressure", UriKind.Relative), cancellationToken);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var lag = body.RootElement.GetProperty("sender_durable_lag_seq");
        return lag.ValueKind == JsonValueKind.Null ? null : lag.GetInt64();
    }
}
