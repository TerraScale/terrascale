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
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Memory;
using TerraBase.Features.Pressure;
using TerraBase.Features.Qos;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Wire;

/// <summary>
/// TerraBase wire protocol v1 (docs/terrabase/wire-protocol.md): one persistent TCP connection per client, length-prefixed
/// little-endian frames, many requests in flight matched by request id. The API key is verified once in the handshake and
/// again whenever the catalog commits; every request runs the HTTP API's admission, validation, metering and durability.
/// </summary>
internal sealed class WireConnectionHandler(IServiceProvider services) : ConnectionHandler
{
    public const byte Version = 1;
    public const byte Hello = 0, Get = 1, Put = 2;

    /// <summary>Requests one connection runs at once; the next frame is read when one answers.</summary>
    public const int MaxInFlight = 128;

    private const int MaxFrameBytes = RequestJson.MaxDocumentBytes + 1024;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly Unauthorized Denied = new("the API key is unknown, expired or revoked, or lacks documents:read", "invalid_key");

    private readonly union Access(Grant, Unauthorized, Validation, NotFound, Unavailable);

    private sealed record Grant(ApiKey Key, TenantContext Database, long Sequence);

    private sealed record Frame(uint Id, byte Op, byte[] Body, IMemoryReservation Reservation);

    // An answer to write, and what to release once it is written: the request's slot and memory.
    private sealed record Outgoing(byte[] Answer, Action? Release = null);

    /// <inheritdoc />
    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        // Cancelled to stop reading frames (a revoked key); requests already running finish and answer.
        using var closing = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed);
        // Bounded by the slots: a request keeps its slot until its answer is written.
        var answers = Channel.CreateUnbounded<Outgoing>(new UnboundedChannelOptions { SingleReader = true });
        var writing = WriteAsync(connection.Transport.Output, answers.Reader, connection.ConnectionClosed);
        var slots = new SemaphoreSlim(MaxInFlight, MaxInFlight);
        try
        {
            if (await HandshakeAsync(connection.Transport.Input, answers.Writer, closing.Token).ConfigureAwait(false) is not { } session)
            {
                return;
            }

            // Requests to one document run in arrival order: each starts after the connection's earlier put to it answered.
            // Requests to different documents run concurrently, so pipelined puts share the engine's group commit.
            var puts = new Dictionary<string, Task>(StringComparer.Ordinal);
            while (true)
            {
                await slots.WaitAsync(closing.Token).ConfigureAwait(false);
                Frame? next;
                try
                {
                    next = await ReadFrameAsync(connection.Transport.Input, session.Grant.Database, closing.Token).ConfigureAwait(false);
                }
                catch
                {
                    slots.Release();
                    throw;
                }

                if (next is not { } frame)
                {
                    slots.Release();
                    break;
                }

                var address = frame.Body.Length > 0 && frame.Body.Length >= 1 + frame.Body[0] + 26 ? Encoding.UTF8.GetString(frame.Body, 0, 1 + frame.Body[0] + 26) : "";
                Task? prior;
                lock (puts)
                {
                    prior = puts.GetValueOrDefault(address);
                }

                // On the thread pool: a request's synchronous storage work never holds up reading the next frame.
                var running = Task.Run(() => RunAsync(session, frame, prior ?? Task.CompletedTask, slots, answers.Writer, closing, connection.ConnectionClosed), CancellationToken.None);
                if (frame.Op == Put)
                {
                    lock (puts)
                    {
                        puts[address] = running;
                    }

                    _ = running.ContinueWith(
                        done =>
                        {
                            lock (puts)
                            {
                                if (puts.GetValueOrDefault(address) == done)
                                {
                                    puts.Remove(address);
                                }
                            }
                        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
        catch (InvalidDataException error)
        {
            // The stream cannot be read past a bad frame: answer it and end the session.
            answers.Writer.TryWrite(new Outgoing(Error(0, new Validation(error.Message, "invalid_frame"))));
        }
        catch (QosMemoryException)
        {
            answers.Writer.TryWrite(new Outgoing(Error(0, new QuotaExceeded("organization memory budget exceeded", "tenant_memory"))));
        }
        catch (Exception error) when (error is OperationCanceledException or IOException)
        {
            // Closed by the client, a revoked key, the shutdown drain or the handshake timeout.
        }
        finally
        {
            for (var i = 0; i < MaxInFlight; i++)
            {
                await slots.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            answers.Writer.TryComplete();
            await writing.ConfigureAwait(false);
            slots.Dispose();
        }
    }

    private sealed record Session(string Secret, string Project, string Database)
    {
        public required Grant Grant { get; set; }
    }

    private async ValueTask<Session?> HandshakeAsync(PipeReader input, ChannelWriter<Outgoing> answers, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);
        var frame = await ReadFrameAsync(input, null, timeout.Token).ConfigureAwait(false) ?? throw new EndOfStreamException();
        using (frame.Reservation)
        {
            if (frame.Op != Hello || frame.Body.Length < 1 || frame.Body[0] != Version)
            {
                answers.TryWrite(new Outgoing(Error(frame.Id, new Validation("the first frame must be a hello for protocol version 1", "unsupported_version"))));
                return null;
            }

            var offset = 1;
            var (secret, project, database) = (Text(frame.Body, ref offset), Text(frame.Body, ref offset), Text(frame.Body, ref offset));
            var access = await AuthorizeAsync(secret, project, database, timeout.Token).ConfigureAwait(false);
            if (access is not Grant grant)
            {
                answers.TryWrite(new Outgoing(Error(frame.Id, Refusal(access))));
                return null;
            }

            answers.TryWrite(new Outgoing(Answer(frame.Id, 200, [Version], [])));
            return new Session(secret, project, database) { Grant = grant };
        }
    }

    // The checks of an API-key request on the client listener: the key and its scope, the database in the key's reach, and
    // its placement in this region. The catalog commit sequence is taken first, so a commit racing the reads re-runs them.
    private async ValueTask<Access> AuthorizeAsync(string secret, string project, string database, CancellationToken cancellationToken)
    {
        var sequence = (await services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false)).CommitSequence;
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
            _ => new Grant(key, tenant, sequence),
        };
    }

    // The session's grant, verified again after any catalog commit (revocation, deletion, placement) or at key expiry.
    private async ValueTask<Access> CurrentAsync(Session session, CancellationToken cancellationToken)
    {
        var grant = session.Grant;
        var sequence = (await services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false)).CommitSequence;
        if (sequence == grant.Sequence && !(grant.Key.ExpiresAt <= services.GetRequiredService<TimeProvider>().GetUtcNow()))
        {
            return grant;
        }

        var access = await AuthorizeAsync(session.Secret, session.Project, session.Database, cancellationToken).ConfigureAwait(false);
        if (access is Grant fresh && fresh.Database.DatabaseId == grant.Database.DatabaseId)
        {
            session.Grant = fresh;
            return fresh;
        }

        // A deleted and recreated database is another database: the session stays bound to the one it authorized.
        return access is Grant ? DocumentRoute.UnknownDatabase : access;
    }

    private async Task RunAsync(
        Session session, Frame frame, Task prior, SemaphoreSlim slots, ChannelWriter<Outgoing> answers, CancellationTokenSource closing, CancellationToken closed)
    {
        byte[]? answer = null;
        IMemoryReservation? held = null;
        try
        {
            await prior.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            answer = await ExecuteAsync(session, frame, closing, closed).ConfigureAwait(false);
            // Held until written, so a client that stops reading holds at most MaxInFlight admitted answers.
            held = services.GetRequiredService<IMemoryBudget>().TryReserve(MemoryLane.Foreground, answer.Length, session.Grant.Database);
            answer = held is null ? Error(frame.Id, new QuotaExceeded("organization memory budget exceeded", "tenant_memory")) : answer;
        }
        catch (Exception) when (closed.IsCancellationRequested)
        {
            // The client is gone: there is nobody to answer.
        }
        finally
        {
            void Release()
            {
                held?.Dispose();
                frame.Reservation.Dispose();
                slots.Release();
            }

            if (answer is null || !answers.TryWrite(new Outgoing(answer, Release)))
            {
                Release();
            }
        }
    }

    private async ValueTask<byte[]> ExecuteAsync(Session session, Frame frame, CancellationTokenSource closing, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        TenantContext? tenant = null;
        var answer = Error(frame.Id, new Unavailable("internal error", "internal_error"));
        try
        {
            if (!services.GetRequiredService<ICatalogGate>().IsOpen)
            {
                return answer = Error(frame.Id, new Unavailable("this machine's catalog is not verified against the other machines yet", "catalog_unverified"));
            }

            var access = await CurrentAsync(session, cancellationToken).ConfigureAwait(false);
            if (access is not Grant grant)
            {
                // Revoked, expired, deleted or moved: refuse this request, read no further frame and end the session.
                await closing.CancelAsync().ConfigureAwait(false);
                return answer = Error(frame.Id, Refusal(access));
            }

            tenant = grant.Database;
            if (frame.Op is not (Get or Put))
            {
                return answer = Error(frame.Id, new Validation("unknown operation", "invalid_frame"));
            }

            if (frame.Op == Put && !grant.Key.Scopes.Contains(ApiKeyScopes.DocumentsWrite))
            {
                return answer = Error(frame.Id, new Forbidden("the API key lacks documents:write", "forbidden_scope"));
            }

            using var lease = services.GetRequiredService<IQosAdmission>().TryEnter(tenant, out _);
            if (lease is null)
            {
                return answer = Error(frame.Id, new QuotaExceeded("organization request budget exceeded", "tenant_busy"));
            }

            services.GetRequiredService<IDocumentCacheQuota>().Reconcile(tenant);
            using var scope = QosContext.Enter(tenant);
            return answer = await DocumentAsync(tenant, frame, cancellationToken).ConfigureAwait(false);
        }
        catch (ReplicaCopyFaultedException)
        {
            return answer = Error(frame.Id, new Unavailable("Storage is awaiting certified recovery.", "copy_faulted"));
        }
        catch (StorageBusyException error)
        {
            return answer = Error(frame.Id, new Unavailable(error.Message, "storage_busy"));
        }
        catch (QosMemoryException)
        {
            return answer = Error(frame.Id, new QuotaExceeded("organization memory budget exceeded", "tenant_memory"));
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            services.GetRequiredService<ILogger<WireConnectionHandler>>().ZLogError(error, $"A wire request failed");
            return answer;
        }
        finally
        {
            // Metered like the HTTP request it replaces: one request with its bytes, on top of the document operation itself.
            if (tenant is not null)
            {
                var elapsed = Stopwatch.GetElapsedTime(started);
                var status = BinaryPrimitives.ReadUInt16LittleEndian(answer.AsSpan(8));
                services.GetRequiredService<IUsageMeter>().RecordRequest(tenant, frame.Op == Put ? "PUT" : "GET", status, elapsed, frame.Body.Length, answer.Length - 10);
                services.GetRequiredService<IPressureSignals>().Record(TerraBaseListener.Client, elapsed, tenant.OrganizationId, tenant.Plan);
            }
        }
    }

    // Body: u8 collection length, the collection, the document's 26-character ULID, then (put only) the JSON document.
    private async ValueTask<byte[]> DocumentAsync(TenantContext tenant, Frame frame, CancellationToken cancellationToken)
    {
        var body = frame.Body;
        var length = body.Length > 0 ? body[0] : 0;
        if (body.Length < 1 + length + 26)
        {
            return Error(frame.Id, new Validation("the frame is too short for a collection and a document id", "invalid_frame"));
        }

        var checkedCollection = DocumentRoute.CheckCollectionName(Encoding.UTF8.GetString(body, 1, length));
        var checkedId = DocumentRoute.CheckDocumentId(Encoding.ASCII.GetString(body, 1 + length, 26));
        if (checkedCollection is Validation badCollection)
        {
            return Error(frame.Id, badCollection);
        }

        if (checkedId is Validation badId)
        {
            return Error(frame.Id, badId);
        }

        var key = (checkedCollection, checkedId) is (string collection, string id) ? new DocumentKey(tenant, collection, id) : throw new UnreachableException();
        await DocumentRoute.CheckCopyAsync(services, tenant, cancellationToken).ConfigureAwait(false);
        var documents = services.GetRequiredService<IDocumentService>();
        if (frame.Op == Get)
        {
            return await documents.GetAsync(key, cancellationToken).ConfigureAwait(false) switch
            {
                StoredDocument { UniqueConflict: true } => Error(frame.Id, new Conflict("the document lost a replicated uniqueness conflict", "unique_violation")),
                StoredDocument { Deleted: false } document => Answer(
                    frame.Id, 200, Short(ETags.Version(document.Scope, key.Collection, document.Id, document.Version, document.Json, document.Deleted)), document.Json.Span),
                StoredDocument or NeverWritten => Error(frame.Id, new NotFound("the document does not exist")),
                NotFound error => Error(frame.Id, error),
                Validation error => Error(frame.Id, error),
            };
        }

        var json = body.AsMemory(1 + length + 26).ToArray();
        if (!RequestJson.IsText(json))
        {
            return Error(frame.Id, new Validation(RequestJson.TextRule));
        }

        var none = new Precondition(null, IfMatchAny: false, IfNoneMatch: false);
        return await documents.WriteConfirmedAsync(tenant, [new PutOperation(key.Collection, key.Id, json, none)], indexErrors: false, cancellationToken).ConfigureAwait(false) switch
        {
            DocumentRejected rejected => Error(frame.Id, rejected.Error),
            WriteResult written => Answer(
                frame.Id, written.Results[0].Created ? (ushort)201 : (ushort)200,
                [(byte)(written.Durability == WriteResult.RemoteDurable ? 1 : 0), .. Short(written.Results[0].ETag!)], []),
        };
    }

    // A frame: u32 length of the rest, u32 request id, u8 operation, body. The memory budget admits it before it is read.
    // The handshake (no tenant yet) is at most 4 KiB.
    private async ValueTask<Frame?> ReadFrameAsync(PipeReader input, TenantContext? tenant, CancellationToken cancellationToken)
    {
        var read = await input.ReadAtLeastAsync(4, cancellationToken).ConfigureAwait(false);
        if (read.Buffer.Length < 4)
        {
            input.AdvanceTo(read.Buffer.Start, read.Buffer.End);
            return read.IsCompleted && read.Buffer.IsEmpty ? null : throw new EndOfStreamException();
        }

        Span<byte> header = stackalloc byte[4];
        read.Buffer.Slice(0, 4).CopyTo(header);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        input.AdvanceTo(read.Buffer.GetPosition(4));
        if (length < 5 || length > (tenant is null ? 4096 : MaxFrameBytes))
        {
            throw new InvalidDataException("frame length out of bounds");
        }

        var reservation = services.GetRequiredService<IMemoryBudget>().TryReserve(MemoryLane.Foreground, 16 * 1024 + length * 3, tenant) ?? throw new QosMemoryException();
        try
        {
            read = await input.ReadAtLeastAsync((int)length, cancellationToken).ConfigureAwait(false);
            if (read.Buffer.Length < length)
            {
                input.AdvanceTo(read.Buffer.Start, read.Buffer.End);
                throw new EndOfStreamException();
            }

            var frame = read.Buffer.Slice(0, length).ToArray();
            input.AdvanceTo(read.Buffer.GetPosition(length));
            return new Frame(BinaryPrimitives.ReadUInt32LittleEndian(frame), frame[4], frame[5..], reservation);
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
    }

    // One writer per connection: answers in completion order, flushed whenever no other answer is ready, then released.
    // Once the client is gone, answers are released unwritten until the connection ends.
    private static async Task WriteAsync(PipeWriter output, ChannelReader<Outgoing> answers, CancellationToken cancellationToken)
    {
        var batch = new List<Outgoing>();
        var gone = false;
        while (await answers.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            while (answers.TryRead(out var answer))
            {
                if (!gone)
                {
                    output.Write(answer.Answer);
                }

                batch.Add(answer);
            }

            try
            {
                gone = gone || (await output.FlushAsync(cancellationToken).ConfigureAwait(false)).IsCompleted;
            }
            catch (Exception error) when (error is OperationCanceledException or IOException)
            {
                gone = true;
            }

            batch.ForEach(answer => answer.Release?.Invoke());
            batch.Clear();
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

    private static byte[] Short(string text) => [checked((byte)text.Length), .. Encoding.ASCII.GetBytes(text)];

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

    // An error answer: the HTTP API's status and error code, then the message.
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
        return Answer(id, (ushort)status, Short(code), Encoding.UTF8.GetBytes(message ?? ""));
    }
}
