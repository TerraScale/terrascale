using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Connections;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Documents;
using TerraBase.Features.Management;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Wire;

/// <summary>
/// TerraBase wire protocol v1 (docs/terrabase/wire-protocol.md): one persistent TCP connection per client, length-prefixed
/// little-endian frames, requests matched to answers by id. The first frame is a hello that verifies an API key against a
/// database as the client listener does.
/// </summary>
internal sealed class WireConnectionHandler(IServiceProvider services) : ConnectionHandler
{
    public const byte Version = 1;
    public const byte Hello = 0;

    private const int MaxFrameBytes = RequestJson.MaxDocumentBytes + 1024;
    private const int MaxHelloBytes = 4096;
    // Answers waiting for the socket; a client that stops reading stops the reading of its frames.
    private const int MaxQueuedAnswers = 128;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly Unauthorized Denied = new("the API key is unknown, expired or revoked, or lacks documents:read", "invalid_key");

    private readonly union Access(Grant, Unauthorized, Validation, NotFound, Unavailable);

    private sealed record Grant(ApiKey Key, TenantContext Database);

    private sealed record Frame(uint Id, byte Op, byte[] Body);

    /// <inheritdoc />
    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var input = connection.Transport.Input;
        var answers = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(MaxQueuedAnswers) { SingleReader = true, SingleWriter = true });
        var writing = WriteAsync(connection.Transport.Output, answers.Reader, connection.ConnectionClosed);
        try
        {
            if (await HandshakeAsync(input, answers.Writer, connection.ConnectionClosed).ConfigureAwait(false) is not { } grant)
            {
                return;
            }

            while (await ReadFrameAsync(input, grant.Database, connection.ConnectionClosed).ConfigureAwait(false) is { } frame)
            {
                await answers.Writer.WriteAsync(Error(frame.Id, new Validation("unknown operation", "unknown_operation")), connection.ConnectionClosed).ConfigureAwait(false);
            }
        }
        catch (InvalidDataException error)
        {
            // The stream cannot be read past a bad frame: answer it and end the session.
            answers.Writer.TryWrite(Error(0, new Validation(error.Message, "invalid_frame")));
        }
        catch (QosMemoryException)
        {
            answers.Writer.TryWrite(Error(0, new QuotaExceeded("organization memory budget exceeded", "tenant_memory")));
        }
        catch (Exception error) when (error is OperationCanceledException or IOException)
        {
            // Closed by the client, the shutdown drain or the handshake timeout.
        }
        finally
        {
            answers.Writer.TryComplete();
            await writing.ConfigureAwait(false);
        }
    }

    private async ValueTask<Grant?> HandshakeAsync(PipeReader input, ChannelWriter<byte[]> answers, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);
        var frame = await ReadFrameAsync(input, null, timeout.Token).ConfigureAwait(false) ?? throw new EndOfStreamException();
        if (frame.Op != Hello || frame.Body.Length < 1 || frame.Body[0] != Version)
        {
            answers.TryWrite(Error(frame.Id, new Validation("the first frame must be a hello for protocol version 1", "unsupported_version")));
            return null;
        }

        var offset = 1;
        var (secret, project, database) = (Text(frame.Body, ref offset), Text(frame.Body, ref offset), Text(frame.Body, ref offset));
        var access = await AuthorizeAsync(secret, project, database, timeout.Token).ConfigureAwait(false);
        if (access is not Grant grant)
        {
            answers.TryWrite(Error(frame.Id, Refusal(access)));
            return null;
        }

        answers.TryWrite(Answer(frame.Id, 200, [Version], []));
        return grant;
    }

    // The checks of an API-key request on the client listener: the key and its scope, the database in the key's reach, and
    // its placement in this region.
    private async ValueTask<Access> AuthorizeAsync(string secret, string project, string database, CancellationToken cancellationToken)
    {
        if (await services.GetRequiredService<IApiKeyStore>().FindAsync(secret, cancellationToken).ConfigureAwait(false) is not ApiKey key
            || !key.Scopes.Contains(ApiKeyScopes.DocumentsRead))
        {
            return Denied;
        }

        if (!EntityIds.IsValid(project) || !DatabaseName.TryParse(database, out var name))
        {
            return new Validation("project must be a canonical uppercase ULID and database a lowercase name of at most 63 characters");
        }

        var found = await services.GetRequiredService<ITenantDirectory>().FindDatabaseAsync(key.OrganizationId, project, name.Value, cancellationToken).ConfigureAwait(false);
        if (found is not TenantContext tenant || !key.Reaches(tenant))
        {
            return found is Unavailable unavailable ? unavailable : DocumentRoute.UnknownDatabase;
        }

        var region = services.GetRequiredService<TerraBaseOptions>().Region;
        return await services.GetRequiredService<IManagementService>().FindDatabaseRecordAsync(key.OrganizationId, project, name.Value, cancellationToken).ConfigureAwait(false) switch
        {
            ManagedDatabase record when !record.PlacedIn(region) => new Unavailable($"the database is served in {record.Home(region)}", "wrong_region"),
            Unavailable error => error,
            _ => new Grant(key, tenant),
        };
    }

    // A frame: u32 length of the rest, u32 request id, u8 operation, body. The memory budget admits it while it is read; the
    // hello (no tenant yet) is at most 4 KiB.
    private async ValueTask<Frame?> ReadFrameAsync(PipeReader input, TenantContext? tenant, CancellationToken cancellationToken)
    {
        var read = await input.ReadAtLeastAsync(4, cancellationToken).ConfigureAwait(false);
        if (read.Buffer.Length < 4)
        {
            input.AdvanceTo(read.Buffer.Start, read.Buffer.End);
            return read.IsCompleted && read.Buffer.IsEmpty ? null : throw new EndOfStreamException();
        }

        var head = new byte[5];
        read.Buffer.Slice(0, 4).CopyTo(head);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(head);
        input.AdvanceTo(read.Buffer.GetPosition(4));
        if (length < 5 || length > (tenant is null ? MaxHelloBytes : MaxFrameBytes))
        {
            throw new InvalidDataException("frame length out of bounds");
        }

        using var reservation = services.GetRequiredService<IMemoryBudget>().TryReserve(MemoryLane.Foreground, 16 * 1024 + length * 2, tenant)
            ?? throw new QosMemoryException();
        read = await input.ReadAtLeastAsync((int)length, cancellationToken).ConfigureAwait(false);
        if (read.Buffer.Length < length)
        {
            input.AdvanceTo(read.Buffer.Start, read.Buffer.End);
            throw new EndOfStreamException();
        }

        // One copy: the request id and operation are read in place, only the body is kept.
        read.Buffer.Slice(0, 5).CopyTo(head);
        var body = read.Buffer.Slice(5, length - 5).ToArray();
        input.AdvanceTo(read.Buffer.GetPosition(length));
        return new Frame(BinaryPrimitives.ReadUInt32LittleEndian(head), head[4], body);
    }

    // One writer per connection: answers in order, flushed whenever no other answer is ready.
    private static async Task WriteAsync(PipeWriter output, ChannelReader<byte[]> answers, CancellationToken cancellationToken)
    {
        try
        {
            while (await answers.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                while (answers.TryRead(out var answer))
                {
                    output.Write(answer);
                }

                if ((await output.FlushAsync(cancellationToken).ConfigureAwait(false)).IsCompleted)
                {
                    return;
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException)
        {
            // The client is gone: nobody reads the rest.
        }
    }

    // A u16 length-prefixed UTF-8 string of a hello body.
    private static string Text(byte[] body, ref int offset)
    {
        var length = body.Length >= offset + 2 ? BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset)) : int.MaxValue;
        var text = body.Length - offset - 2 >= length ? Encoding.UTF8.GetString(body, offset + 2, length) : throw new InvalidDataException("the hello frame is truncated");
        offset += 2 + length;
        return text;
    }

    private static Failure Refusal(Access access) => access switch
    {
        Unauthorized error => error,
        Validation error => error,
        NotFound error => error,
        Unavailable error => error,
        Grant => throw new UnreachableException(),
    };

    // An answer: u32 length of the rest, u32 request id, u16 status (the HTTP API's), head, body.
    private static byte[] Answer(uint id, ushort status, ReadOnlySpan<byte> head, ReadOnlySpan<byte> body)
    {
        var answer = new byte[10 + head.Length + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(answer, (uint)(answer.Length - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(answer.AsSpan(4), id);
        BinaryPrimitives.WriteUInt16LittleEndian(answer.AsSpan(8), status);
        head.CopyTo(answer.AsSpan(10));
        body.CopyTo(answer.AsSpan(10 + head.Length));
        return answer;
    }

    // An error answer: the HTTP API's status, u8 error code length, the error code, then the message.
    private static byte[] Error(uint id, Failure error)
    {
        var (status, code, message) = error switch
        {
            NotFound e => (404, e.Code, e.Message),
            Conflict e => (409, e.Code, e.Message),
            Validation e => (400, e.Code, e.Message),
            PreconditionFailed e => (412, e.Code, e.Message),
            Unauthorized e => (401, e.Code, e.Message),
            Forbidden e => (403, e.Code, e.Message),
            QuotaExceeded e => (429, e.Code, e.Message),
            Unavailable { Code: "wrong_region" } e => (421, e.Code, e.Message),
            Unavailable e => (503, e.Code, e.Message),
            PayloadTooLarge e => (413, e.Code, e.Message),
            UnsupportedMediaType e => (415, e.Code, e.Message),
            RequestTimeout e => (408, e.Code, e.Message),
            Gone e => (410, e.Code, e.Message),
            OutcomeUnknown e => (503, e.Code, e.Message),
        };
        return Answer(id, (ushort)status, [checked((byte)code.Length), .. Encoding.ASCII.GetBytes(code)], Encoding.UTF8.GetBytes(message ?? ""));
    }
}
