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
    private const string PreviousLayout = "previous_layout";
    private const int NoStream = 10059;
    private static readonly TimeSpan ParkDuplicateWindow = TimeSpan.FromDays(7);
    private static readonly string[] ParkReasons = ["malformed", "empty", "deleted_database"];
    private Measurement<long>[] _parkedCounts = [.. ParkReasons.Append(PreviousLayout).Select(reason => new Measurement<long>(0, new KeyValuePair<string, object?>("reason", reason)))];
    private long _previousParked;

    private async Task ParkAsync(string origin, string source, ulong sequence, string pushed, byte[]? payload, string reason, CancellationToken cancellationToken)
    {
        var js = _js ?? throw new IOException("the push connection is not open");
        var database = PushNames.Database(pushed);
        var record = new ParkedPush(origin, source, sequence, pushed,
            database is null ? null : $"{database.OrganizationId}/{database.ProjectId}/{database.DatabaseId}",
            reason, time.GetUtcNow(), payload ?? []);
        _ = await js.CreateOrUpdateStreamAsync(new StreamConfig(PushNames.ParkStream(options.Region), [PushNames.ParkSubjects(options.Region)])
        {
            Storage = StreamConfigStorage.File,
            Retention = StreamConfigRetention.Limits,
            Discard = StreamConfigDiscard.New,
            MaxBytes = 1L << 30,
            MaxMsgSize = 2 * MaxPayloadBytes,
            DuplicateWindow = ParkDuplicateWindow,
            NumReplicas = 1,
        }, cancellationToken).ConfigureAwait(false);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        var subject = $"terrabase.push-parked.{options.Region}.{reason}.{origin}.{identity}.{sequence.ToString(CultureInfo.InvariantCulture)}";
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
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.ZLogError($"The durable parked push count could not be read: {error.GetType().Name:@Error}");
            }
        }
    }

    private async Task RefreshParkedCountsAsync(NatsJSContext js, CancellationToken cancellationToken)
    {
        var subjects = (await ParkStateAsync(js, PushNames.ParkStream(options.Region), ">", cancellationToken).ConfigureAwait(false))?.Subjects;
        var previous = (await ParkStateAsync(js, PushNames.PreviousParkStream(options.Region), null, cancellationToken).ConfigureAwait(false))?.Messages ?? 0;
        var counts = ParkReasons.Select(reason => new Measurement<long>(
            checked((long)(subjects?.Where(subject => subject.Key.Split('.') is [_, _, _, var label, ..] && label == reason).Sum(subject => (decimal)subject.Value) ?? 0)),
            new KeyValuePair<string, object?>("reason", reason)))
            .Append(new Measurement<long>(previous, new KeyValuePair<string, object?>("reason", PreviousLayout))).ToArray();
        Volatile.Write(ref _parkedCounts, counts);
        if (Interlocked.Exchange(ref _previousParked, previous) != previous && previous > 0)
        {
            logger.ZLogWarning($"The parked stream of the previous layout {PushNames.PreviousParkStream(options.Region):@Stream} holds {previous:@Records} records; they are kept as they are");
        }
    }

    private static async Task<StreamState?> ParkStateAsync(NatsJSContext js, string name, string? subjects, CancellationToken cancellationToken)
    {
        try
        {
            return (await js.GetStreamAsync(name, subjects is null ? null : new StreamInfoRequest { SubjectsFilter = subjects }, cancellationToken).ConfigureAwait(false)).Info.State;
        }
        catch (NatsJSApiException error) when (error.Error.ErrCode == NoStream)
        {
            return null;
        }
    }
}
