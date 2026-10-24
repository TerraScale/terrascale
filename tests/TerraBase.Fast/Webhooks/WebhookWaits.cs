using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraBase.Engine;
using TerraBase.Testing;

namespace TerraBase.Fast.Webhooks;

// Fake time deliberately stands still while work finishes. Authenticated polling would spend the Hobby burst
// without replenishing it; wait for the receiver or a catalog commit instead, then observe through the real API.
internal static class WebhookWaits
{
    public static async Task<WebhookRequest> WaitForRequestAsync(this WebhookApi fixture, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var request = fixture.Receiver.ReadAsync(deadline.Token).AsTask();
        if (!request.IsCompleted)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        return await request.WaitAsync(deadline.Token);
    }

    public static async Task<JsonElement> WaitForDeliveryAsync(this WebhookApi fixture, string webhook, string status,
        CancellationToken token, string? error = null)
    {
        var found = default(JsonElement);
        await ObserveAfterCommitAsync(fixture, async cancellation =>
        {
            var list = await fixture.JsonAsync($"{fixture.Path}/{webhook}/deliveries", cancellation);
            if (list.GetArrayLength() == 0)
            {
                return false;
            }

            found = list[0];
            return found.GetProperty("status").GetString() == status
                && (error is null || found.GetProperty("errorCode").GetString() == error);
        }, token);
        return found;
    }

    public static async Task DriveAfterCommitAsync(this WebhookApi fixture, Func<CancellationToken, Task<bool>> condition,
        CancellationToken token)
    {
        if (await condition(token))
        {
            return;
        }

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await ObserveAfterCommitAsync(fixture, condition, token);
    }

    private static async Task ObserveAfterCommitAsync(WebhookApi fixture, Func<CancellationToken, Task<bool>> condition,
        CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var system = await fixture.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(deadline.Token);
        while (true)
        {
            // Capture before reading so a commit between the observation and the wait cannot be missed.
            var sequence = system.CommitSequence;
            if (await condition(deadline.Token))
            {
                return;
            }

            await system.WaitForCommitAsync(sequence, deadline.Token);
        }
    }
}

internal sealed class WebhookFailureLog : ILoggerProvider
{
    public TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ILogger CreateLogger(string categoryName) => new Logger(Failed);
    public void Dispose() { }

    private sealed class Logger(TaskCompletionSource failed) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("Webhook delivery failed; persisted work will retry", StringComparison.Ordinal))
            {
                failed.TrySetResult();
            }
        }
    }
}
