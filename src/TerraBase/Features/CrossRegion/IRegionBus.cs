using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NATS.Client.Core;
using TerraBase.Configuration;
using TerraBase.Engine.Cluster;

namespace TerraBase.Features.CrossRegion;

/// <summary>
/// Request/reply between regions: core NATS over ts-orchestrator's fabric in production. Requests and replies are at
/// most <see cref="WanProtocol.MaxMessageBytes"/>.
/// </summary>
public interface IRegionBus
{
    /// <summary>Sends <paramref name="request"/> to every responder of <paramref name="subject"/> and yields their replies as they come.</summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> RequestManyAsync(string subject, ReadOnlyMemory<byte> request, CancellationToken cancellationToken);

    /// <summary>The reply of one responder of <paramref name="subject"/>; throws when none answers in time.</summary>
    ValueTask<ReadOnlyMemory<byte>> RequestAsync(string subject, ReadOnlyMemory<byte> request, CancellationToken cancellationToken);

    /// <summary>Answers every request on <paramref name="subject"/> with <paramref name="answer"/> until <paramref name="cancellationToken"/> ends.</summary>
    Task ServeAsync(string subject, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>> answer, CancellationToken cancellationToken);

    /// <summary>The cell this member serves, from its NATS credentials; null when they name none.</summary>
    string? Cell { get; }

    /// <summary>Publishes <paramref name="data"/> on <paramref name="subject"/>, without a reply.</summary>
    ValueTask PublishAsync(string subject, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
}

/// <summary>The region bus on ts-orchestrator's NATS (<c>nats-url</c>).</summary>
internal sealed class NatsRegionBus(NatsSettings settings, ILoggerFactory loggers) : IRegionBus, IAsyncDisposable
{
    // gru to sfo is about 170 ms round trip; a member that has not answered the heads within this is skipped this round.
    private static readonly NatsSubOpts Many = new() { Timeout = TimeSpan.FromSeconds(2), IdleTimeout = TimeSpan.FromMilliseconds(500) };
    private static readonly NatsSubOpts One = new() { Timeout = TimeSpan.FromSeconds(10) };

    // The options of the member store's connection (NatsMemberStore): credentials and certificates read on every connect.
    private readonly NatsConnection _connection = new(new NatsOpts
    {
        Url = settings.Url,
        Name = "terrabase",
        AuthOpts = new NatsAuthOpts { AuthCredCallback = (_, _) => ValueTask.FromResult(NatsAuthCred.FromCreds(File.ReadAllText(settings.CredentialsFile))) },
        TlsOpts = settings.CertificateFile is null
            ? new NatsTlsOpts()
            : new NatsTlsOpts { Mode = TlsMode.Implicit, ConfigureClientAuthentication = options => NatsMemberStore.ConfigureTls(settings, options) },
        IgnoreAuthErrorAbort = true,
        LoggerFactory = loggers,
    });

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> RequestManyAsync(string subject, ReadOnlyMemory<byte> request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var reply in _connection.RequestManyAsync<ReadOnlyMemory<byte>, byte[]>(subject, request, replyOpts: Many, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (reply.Data is { } data)
            {
                yield return data;
            }
        }
    }

    public async ValueTask<ReadOnlyMemory<byte>> RequestAsync(string subject, ReadOnlyMemory<byte> request, CancellationToken cancellationToken) =>
        (await _connection.RequestAsync<ReadOnlyMemory<byte>, byte[]>(subject, request, replyOpts: One, cancellationToken: cancellationToken).ConfigureAwait(false)).Data
        ?? throw new IOException("the responder sent an empty reply");

    public async Task ServeAsync(string subject, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>> answer, CancellationToken cancellationToken)
    {
        // ponytail: one request at a time per subject; answer concurrently if several regions pull one member at once.
        await foreach (var request in _connection.SubscribeAsync<byte[]>(subject, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (request.ReplyTo is not null)
            {
                await request.ReplyAsync(await answer(request.Data ?? [], cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public ValueTask PublishAsync(string subject, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        _connection.PublishAsync<ReadOnlyMemory<byte>>(subject, data, cancellationToken: cancellationToken);

    // Read until found, then kept: the credentials file is re-read only while it names no cell.
    public string? Cell
    {
        get => field ??= ReadCell();
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    // The first cell: tag of the user JWT, which TS-803's role templates scope subjects with as {{tag(cell)}}.
    private string? ReadCell()
    {
        try
        {
            var jwt = File.ReadLines(settings.CredentialsFile).SkipWhile(line => line != "-----BEGIN NATS USER JWT-----").Skip(1).FirstOrDefault();
            var payload = jwt?.Split('.') is [_, { } segment, _] ? Base64Url.DecodeFromChars(segment) : null;
            using var claims = payload is null ? null : JsonDocument.Parse(payload);
            if (claims is null || !claims.RootElement.TryGetProperty("nats", out var nats) || !nats.TryGetProperty("tags", out var tags)
                || tags.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var cell = tags.EnumerateArray().Select(tag => tag.GetString()).FirstOrDefault(tag => tag?.StartsWith("cell:", StringComparison.Ordinal) == true);
            return cell?[5..] is { } token && WanSettings.IsToken(token) ? token : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or JsonException)
        {
            // The file itself is never logged: it holds the user's seed.
            return null;
        }
    }
}
