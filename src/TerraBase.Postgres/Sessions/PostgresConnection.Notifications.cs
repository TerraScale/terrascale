using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TerraBase.Postgres.Hosting;
using TerraBase.Postgres.Protocol;
using TerraBase.Postgres.Sql;
using TerraScale.Contracts.Consistency;
using TerraScale.Contracts.Continuity;

namespace TerraBase.Postgres.Sessions;

internal sealed partial class PostgresConnection
{
    private async ValueTask<PgOutcome> ListenAsync(PgListenCommand command, CancellationToken cancellationToken)
    {
        if (command.Channel.Length > PostgresLimits.MaxChannelLength)
        {
            return new PgError("42622", $"channel name \"{command.Channel}\" is too long");
        }

        if (!session.Channels.Contains(command.Channel) && session.Channels.Count >= PostgresLimits.MaxChannels)
        {
            return PgErrors.TooManyChannels;
        }

        if (session.SubscriptionTask is null || session.SubscriptionTask.IsCompleted)
        {
            if (await session.Executor.EnsureUsableAsync(cancellationToken) is { } busy)
            {
                return PgErrors.From(busy);
            }

            session.Subscription?.Dispose();
            var subscription = CancellationTokenSource.CreateLinkedTokenSource(closed);
            session.Subscription = subscription;
            session.SubscriptionTask = PumpAsync(session.Executor, session.Executor.CurrentBookmark, session.Notifications.Writer, Reader, subscription.Token);
        }

        session.Channels.Add(command.Channel);
        return PgResult.Command("LISTEN");
    }

    private async ValueTask<PgOutcome> UnlistenAsync(PgUnlistenCommand command)
    {
        if (command.Channel is { } channel)
        {
            session.Channels.Remove(channel);
        }
        else
        {
            session.Channels.Clear();
        }

        if (session.Channels.Count == 0)
        {
            await StopListeningAsync(session);
        }

        return PgResult.Command("UNLISTEN");
    }

    private static async ValueTask StopListeningAsync(PostgresSession session)
    {
        session.Channels.Clear();
        if (session.Subscription is { } subscription)
        {
            await subscription.CancelAsync();
            if (session.SubscriptionTask is { } task)
            {
                await task;
            }

            subscription.Dispose();
        }

        session.Subscription = null;
        session.SubscriptionTask = null;
        while (session.Notifications.Reader.TryRead(out _))
        {
        }
    }

    private static async Task PumpAsync(
        IPostgresDatabaseExecutor executor,
        CausalBookmark after,
        ChannelWriter<ChangefeedEntry> notifications,
        PgFrameReader reader,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var entry in executor.SubscribeChangesAsync(after, cancellationToken))
            {
                await notifications.WriteAsync(entry, cancellationToken);
                reader.Interrupt();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async ValueTask<bool> DeliverNotificationsAsync()
    {
        var delivered = false;
        while (session.Notifications.Reader.TryRead(out var entry))
        {
            var payload = Payload(entry);
            foreach (var channel in session.Channels)
            {
                Writer.Notification(session.ProcessId, channel, payload);
                delivered = true;
                if (Writer.ShouldFlush && !await Writer.FlushAsync(closed))
                {
                    return false;
                }
            }
        }

        return !delivered || await Writer.FlushAsync(closed);
    }

    private static string Payload(ChangefeedEntry entry)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("database", entry.DatabaseId.ToString());
            json.WriteString("tx", entry.TxId.ToString());
            json.WriteString("origin", entry.OriginRegion);
            json.WriteNumber("sequence", entry.Sequence);
            json.WriteNumber("schemaEpoch", entry.SchemaEpoch);
            json.WriteString("mode", ConsistencyHeaders.Format(entry.Mode));
            json.WriteString("committedAt", entry.CommittedAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
