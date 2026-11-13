using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using ZLogger;

namespace TerraBase.Features.HomePush;

internal sealed record ParkedPush(string Origin, string Source, ulong Sequence, string Subject, string? Database, string Reason, DateTimeOffset At, byte[] Payload);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ParkedPush))]
internal sealed partial class ParkedPushJsonContext : JsonSerializerContext;

internal partial class HomePush
{
    private static readonly TimeSpan ParkRetention = TimeSpan.FromDays(7);
    private static readonly string[] ParkReasons = ["malformed", "empty", "deleted_database"];
    private Measurement<long>[] _parkedCounts = EmptyParkedCounts();

    private static Measurement<long>[] EmptyParkedCounts() =>
        [.. ParkReasons.Select(reason => new Measurement<long>(0, new KeyValuePair<string, object?>("reason", reason)))];

    private async Task ParkAsync(PushQueue queue, PushItem item, string reason, CancellationToken cancellationToken)
    {
        var js = _js ?? throw new IOException("the push connection is not open");
        var source = queue.Source;
        var sequence = item.Sequence;
        var origin = queue.Origin;
        var database = PushNames.Database(item.Subject);
        var record = new ParkedPush(origin, source, sequence, item.Subject,
            database is null ? null : $"{database.OrganizationId}/{database.ProjectId}/{database.DatabaseId}",
            reason, time.GetUtcNow(), item.Data ?? []);
        _ = await js.CreateOrUpdateStreamAsync(new StreamConfig(PushNames.ParkStream(options.Region), [PushNames.ParkSubjects(options.Region)])
        {
            Storage = StreamConfigStorage.File,
            Retention = StreamConfigRetention.Limits,
            Discard = StreamConfigDiscard.Old,
            MaxBytes = 1L << 30,
            MaxMsgs = 10_000,
            MaxAge = ParkRetention,
            MaxMsgSize = 2 * MaxPayloadBytes,
            DuplicateWindow = ParkRetention,
            NumReplicas = 1,
        }, cancellationToken).ConfigureAwait(false);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        var subject = $"terrabase.push-park.{options.Region}.{reason}.{origin}.{identity}.{sequence.ToString(CultureInfo.InvariantCulture)}";
        var parked = await js.PublishAsync(subject,
            JsonSerializer.SerializeToUtf8Bytes(record, ParkedPushJsonContext.Default.ParkedPush),
            opts: new NatsJSPubOpts { MsgId = identity + "." + sequence.ToString(CultureInfo.InvariantCulture) }, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (parked.Error is { } error)
        {
            throw new IOException(error.Description);
        }

        if (!parked.Duplicate)
        {
            _parked?.Add(1, new KeyValuePair<string, object?>("reason", reason));
            logger.ZLogWarning($"Push {source:@Source} sequence {sequence:@Sequence} stored in the durable parked stream: {reason:@Reason}");
        }
    }

    private async Task ParkedCountsAsync(NatsJSContext js, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PeerSweep, time);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await RefreshParkedCountsAsync(js, cancellationToken).ConfigureAwait(false);
            }
            catch (NatsJSApiException error) when (error.Error.ErrCode == 10059)
            {
                Volatile.Write(ref _parkedCounts, EmptyParkedCounts());
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.ZLogError($"The durable parked push count could not be read: {error.GetType().Name:@Error}");
            }
        }
    }
    private async Task RefreshParkedCountsAsync(NatsJSContext js, CancellationToken cancellationToken)
    {
        var stream = await js.GetStreamAsync(PushNames.ParkStream(options.Region),
            new StreamInfoRequest { SubjectsFilter = ">" }, cancellationToken).ConfigureAwait(false);
        var subjects = stream.Info.State.Subjects;
        var counts = ParkReasons.Select(reason => new Measurement<long>(
            checked((long)(subjects?.Where(subject => subject.Key.Split('.') is [_, _, _, var label, ..] && label == reason).Sum(subject => (decimal)subject.Value) ?? 0)),
            new KeyValuePair<string, object?>("reason", reason))).ToArray();
        Volatile.Write(ref _parkedCounts, counts);
    }
}
