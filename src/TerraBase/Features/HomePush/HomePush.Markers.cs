using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.HomePush;

internal sealed record PreviousFloor(ulong Floor, ulong Last);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(PreviousFloor))]
internal sealed partial class PreviousFloorJsonContext : JsonSerializerContext;

internal partial class HomePush
{
    private const int WrongLastSequence = 10071;

    private async Task MarkPurgedAsync(NatsJSContext js, TenantContext database, ulong through, CancellationToken cancellationToken)
    {
        var stream = await js.GetStreamAsync(PushNames.Stream(options.Region), cancellationToken: cancellationToken).ConfigureAwait(false);
        var subject = PushNames.Purged(options.Region, database);
        while (true)
        {
            var last = await LastAsync(stream, subject, cancellationToken).ConfigureAwait(false);
            if (last is not null && Through(last) >= through)
            {
                return;
            }

            if (await PublishExpectedAsync(js, subject, Encoding.ASCII.GetBytes(through.ToString(CultureInfo.InvariantCulture)), last?.Seq ?? 0, cancellationToken)
                .ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async Task MarkBuildAsync(NatsJSContext js, CancellationToken cancellationToken)
    {
        var subject = PushNames.Build(options.Region, options.MachineId);
        var stream = await js.GetStreamAsync(PushNames.Stream(options.Region), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (await LastAsync(stream, subject, cancellationToken).ConfigureAwait(false) is null)
        {
            _ = await PublishExpectedAsync(js, subject, "markers"u8.ToArray(), 0, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> OriginMarksAsync(INatsJSStream stream, string origin, CancellationToken cancellationToken)
    {
        IEnumerable<string> nodes = options.PeersFile is null
            ? [options.MachineId]
            : (await new PeersFile(options).ListAsync(cancellationToken).ConfigureAwait(false)).Where(node => node.Region == origin).Select(node => node.Name);
        foreach (var node in nodes)
        {
            if (await LastAsync(stream, PushNames.Build(origin, node), cancellationToken).ConfigureAwait(false) is null)
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<ulong?> PurgedThroughAsync(INatsJSStream stream, string origin, TenantContext database, CancellationToken cancellationToken) =>
        await LastAsync(stream, PushNames.Purged(origin, database), cancellationToken).ConfigureAwait(false) is { } marker ? Through(marker) : null;

    private static ulong Through(StoredMessage marker) =>
        ulong.TryParse(Encoding.ASCII.GetString(marker.Data.ToArray()), NumberStyles.None, CultureInfo.InvariantCulture, out var through)
            ? through
            : throw new IOException("a purge marker of the push stream is not a stream sequence");

    private static async Task<StoredMessage?> LastAsync(INatsJSStream stream, string subject, CancellationToken cancellationToken)
    {
        try
        {
            return (await stream.GetAsync(new StreamMsgGetRequest { LastBySubj = subject }, cancellationToken).ConfigureAwait(false)).Message;
        }
        catch (NatsJSApiException error) when (error.Error.Code == 404 || error.Error.ErrCode == NoMessageFound)
        {
            return null;
        }
    }

    private static async Task<bool> PublishExpectedAsync(NatsJSContext js, string subject, byte[] payload, ulong expected, CancellationToken cancellationToken)
    {
        try
        {
            var ack = await js.PublishAsync(subject, payload, opts: new NatsJSPubOpts { ExpectedLastSubjectSequence = expected }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (ack.Error is { ErrCode: WrongLastSequence })
            {
                return false;
            }

            ack.EnsureSuccess();
            return true;
        }
        catch (NatsJSApiException error) when (error.Error.ErrCode == WrongLastSequence)
        {
            return false;
        }
    }

    private static async Task EnsureStateSubjectsAsync(NatsJSContext js, string origin, CancellationToken cancellationToken)
    {
        var stream = await js.GetStreamAsync(PushNames.Stream(origin), cancellationToken: cancellationToken).ConfigureAwait(false);
        var config = stream.Info.Config;
        if (config.Subjects?.Contains(PushNames.StateSubjects(origin)) == true)
        {
            return;
        }

        config.Subjects = [.. config.Subjects ?? [], PushNames.StateSubjects(origin)];
        _ = await js.UpdateStreamAsync(config, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PreviousFloor> PreviousFloorAsync(NatsJSContext js, string origin, CancellationToken cancellationToken)
    {
        await EnsureStateSubjectsAsync(js, origin, cancellationToken).ConfigureAwait(false);
        var name = PushNames.Stream(origin);
        var own = PushNames.PreviousConsumer(options.Region);
        while (true)
        {
            var stream = await js.GetStreamAsync(name, cancellationToken: cancellationToken).ConfigureAwait(false);
            var record = await LastAsync(stream, PushNames.Upgrade(origin), cancellationToken).ConfigureAwait(false);
            var floors = new Dictionary<string, ulong>(StringComparer.Ordinal);
            await foreach (var consumer in js.ListConsumerNamesAsync(name, cancellationToken).ConfigureAwait(false))
            {
                if (PushNames.PreviousConsumerRegion(consumer) is not null)
                {
                    floors[consumer] = (await js.GetConsumerAsync(name, consumer, cancellationToken).ConfigureAwait(false)).Info.AckFloor?.StreamSeq ?? 0;
                }
            }

            var recorded = record is null ? null : JsonSerializer.Deserialize(record.Data.ToArray(), PreviousFloorJsonContext.Default.PreviousFloor)
                ?? throw new IOException("the push stream's upgrade record is empty");
            if (recorded is null && floors.Count == 0)
            {
                return new PreviousFloor(0, 0);
            }

            if (recorded is not null && !floors.ContainsKey(own))
            {
                return recorded;
            }

            var last = (await js.GetStreamAsync(name, cancellationToken: cancellationToken).ConfigureAwait(false)).Info.State.LastSeq;
            var next = recorded is null ? new PreviousFloor(floors.Values.Min(), last) : recorded with { Last = Math.Max(recorded.Last, last) };
            if (next != recorded && !await PublishExpectedAsync(js, PushNames.Upgrade(origin),
                    JsonSerializer.SerializeToUtf8Bytes(next, PreviousFloorJsonContext.Default.PreviousFloor), record?.Seq ?? 0, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            logger.ZLogWarning($"The push from {origin} starts from the previous build's decided floor {next.Floor:@Floor}; positions through {next.Last:@Last} may have been seen by it; its consumer {own:@Consumer} is deleted");
            await DeleteConsumerAsync(js, name, own, cancellationToken).ConfigureAwait(false);
            return next;
        }
    }

    private async Task<bool> ParkedBeforeAsync(NatsJSContext js, string origin, TenantContext database, ulong sequence, CancellationToken cancellationToken)
    {
        foreach (var region in (await peers.OtherRegionsAsync(cancellationToken).ConfigureAwait(false)).Select(peer => peer.Region).Append(options.Region).Distinct(StringComparer.Ordinal))
        {
            INatsJSStream stream;
            try
            {
                stream = await js.GetStreamAsync(PushNames.PreviousParkStream(region), cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (NatsJSApiException error) when (error.Error.Code == 404 || error.Error.ErrCode == NoStream)
            {
                continue;
            }

            if (await LastAsync(stream, PushNames.PreviousParkSubject(region, origin, database, sequence), cancellationToken).ConfigureAwait(false) is not null)
            {
                return true;
            }
        }

        return false;
    }
}
