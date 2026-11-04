using System.Text.Json;
using NATS.Client.Core;
using TerraBase.Configuration;
using TerraBase.Hosting;

namespace TerraBase.Features.Clock;

internal sealed class ClockReports(TerraBaseOptions options) : IAsyncDisposable
{
    private NatsConnection? _connection;

    public async Task<bool> PublishAsync(ClockReport report, CancellationToken cancellationToken)
    {
        if (options is not { NatsUrl: { } url, NatsToken: { } token })
        {
            return false;
        }

        _connection ??= new NatsConnection(new NatsOpts
        {
            Url = url.ToString(), Name = $"terrabase-clock-{options.MachineId}",
            AuthOpts = new NatsAuthOpts { Token = token }, MaxReconnectRetry = -1,
            ReconnectWaitMin = TimeSpan.FromMilliseconds(100), ReconnectWaitMax = TimeSpan.FromSeconds(2),
        });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            var response = await _connection.RequestAsync<byte[], byte[]>("terrabase.clock.report",
                JsonSerializer.SerializeToUtf8Bytes(report, TerraBaseJsonContext.Default.ClockReport), cancellationToken: timeout.Token).ConfigureAwait(false);
            using var ack = JsonDocument.Parse(response.Data ?? []);
            return ack.RootElement.TryGetProperty("stored", out var stored) && stored.ValueKind == JsonValueKind.True;
        }
        catch (Exception error) when (error is NatsException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
