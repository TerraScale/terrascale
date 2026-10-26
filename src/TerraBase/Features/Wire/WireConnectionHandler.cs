using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
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
using TerraBase.Features.Regions;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Wire;

/// <summary>
/// TerraBase wire protocol v1 (docs/terrabase/wire-protocol.md): one persistent TCP connection per client, length-prefixed
/// little-endian frames, many requests in flight matched to answers by id. The first frame is a hello that verifies an API
/// key against a database as the client listener does, and again whenever the catalog commits; every request runs the HTTP
/// API's admission, validation, metering and durability.
/// </summary>
internal sealed class WireConnectionHandler(IServiceProvider services)
{
    public const byte Version = 1;
    public const byte Hello = 0, Get = 1, Put = 2, PinnedHello = 4;

    /// <summary>Requests one connection runs at once; the next frame is read when one answers.</summary>
    public const int MaxInFlight = 128;

    private const int MaxFrameBytes = RequestJson.MaxDocumentBytes + 1024;
    private const int MaxHelloBytes = 4096;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly Unavailable InternalError = new("internal error", "internal_error");
    private static readonly Unauthorized Denied = new("the API key is unknown, expired or revoked, or lacks documents:read", "invalid_key");

    private readonly union Access(Grant, Unauthorized, Validation, NotFound, Unavailable);

    private sealed record Grant(ApiKey Key, TenantContext Database, long Sequence);

    private sealed record Frame(uint Id, byte Op, byte[] Body, IMemoryReservation Reservation);

    // An answer to write, and what to release once it is written: the request's slot and memory.
    private sealed record Outgoing(byte[] Answer, Action? Release = null);

    private sealed record Session(string Secret, string Project, string Database, string? Pin)
    {
        public required Grant Grant { get; set; }
    }

    /// <summary>Serves one connection until the client, a refusal or the shutdown drain (<paramref name="closed"/>) ends it.</summary>
    public async Task RunAsync(PipeReader input, PipeWriter output, CancellationToken closed)
    {
        // Cancelled to stop reading frames (a revoked key); requests already running finish and answer.
        using var closing = CancellationTokenSource.CreateLinkedTokenSource(closed);
        var outbox = new Outbox(output, closed);
        var slots = new SemaphoreSlim(MaxInFlight, MaxInFlight);
        try
        {
            if (await HandshakeAsync(input, outbox, closing.Token).ConfigureAwait(false) is not { } session)
            {
                return;
            }

            // Requests to one document run in arrival order: each starts after the connection's previous request to it answered.
            // Requests to different documents run concurrently, so pipelined puts share the engine's group commit. Only
            // requests still running are kept, so a request looks its document up only while some other one runs.
            var running = new Dictionary<string, Task>(StringComparer.Ordinal);
            while (true)
            {
                // Answers wait while more whole frames are buffered, so one flush carries a batch; never across a wait.
                if (slots.CurrentCount == 0 || !WholeFrameBuffered(input))
                {
                    await outbox.FlushAsync().ConfigureAwait(false);
                }

                await slots.WaitAsync(closing.Token).ConfigureAwait(false);
                Frame? next;
                try
                {
                    next = await ReadFrameAsync(input, session.Grant.Database, closing.Token).ConfigureAwait(false);
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

                var address = running.Count > 0 ? Address(frame.Body) : null;
                var prior = address is null ? null : GetPrior(running, address);

                // Inline: a request runs on this loop until it first waits (a cache hit never does), then its answer is
                // written by whoever completes it.
                outbox.Deferring = true;
                Task request;
                try
                {
                    request = RequestAsync(session, frame, prior ?? Task.CompletedTask, slots, outbox, closing, closed);
                }
                finally
                {
                    outbox.Deferring = false;
                }

                if (!request.IsCompleted)
                {
                    var key = address ?? Address(frame.Body);
                    lock (running)
                    {
                        running[key] = request;
                    }

                    _ = request.ContinueWith(
                        done =>
                        {
                            lock (running)
                            {
                                if (running.GetValueOrDefault(key) == done)
                                {
                                    running.Remove(key);
                                }
                            }
                        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
        catch (InvalidDataException error)
        {
            // The stream cannot be read past a bad frame: answer it and end the session.
            outbox.Add(new Outgoing(Error(0, new Validation(error.Message, "invalid_frame"))));
        }
        catch (QosMemoryException)
        {
            outbox.Add(new Outgoing(Error(0, new QuotaExceeded("organization memory budget exceeded", "tenant_memory"))));
        }
        catch (Exception error) when (error is OperationCanceledException or IOException)
        {
            // Closed by the client, a revoked key, the shutdown drain or the handshake timeout.
        }
        finally
        {
            await outbox.FlushAsync().ConfigureAwait(false);
            for (var i = 0; i < MaxInFlight; i++)
            {
                await slots.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await outbox.FlushAsync().ConfigureAwait(false);
            slots.Dispose();
        }
    }

    private static Task? GetPrior(Dictionary<string, Task> running, string address)
    {
        lock (running)
        {
            return running.GetValueOrDefault(address);
        }
    }

    // A frame's collection and document id, the key of the per-document order.
    private static string Address(byte[] body) =>
        body.Length > 0 && body.Length >= 1 + body[0] + 26 ? Encoding.UTF8.GetString(body, 0, 1 + body[0] + 26) : "";

    // Whether a whole frame is already buffered, so reading it waits for nothing.
    private static bool WholeFrameBuffered(PipeReader input)
    {
        if (!input.TryRead(out var read))
        {
            return false;
        }

        var buffer = read.Buffer;
        var whole = false;
        if (buffer.Length >= 4)
        {
            Span<byte> header = stackalloc byte[4];
            buffer.Slice(0, 4).CopyTo(header);
            whole = buffer.Length - 4 >= BinaryPrimitives.ReadUInt32LittleEndian(header);
        }

        input.AdvanceTo(buffer.Start);
        return whole;
    }

    private async ValueTask<Session?> HandshakeAsync(PipeReader input, Outbox answers, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);
        var frame = await ReadFrameAsync(input, null, timeout.Token).ConfigureAwait(false) ?? throw new EndOfStreamException();
        using (frame.Reservation)
        {
            if (frame.Op is not (Hello or PinnedHello) || frame.Body.Length < 1 || frame.Body[0] != Version)
            {
                answers.Add(new Outgoing(Error(frame.Id, new Validation("the first frame must be a hello for protocol version 1", "unsupported_version"))));
                return null;
            }

            var offset = 1;
            var (secret, project, database) = (Text(frame.Body, ref offset), Text(frame.Body, ref offset), Text(frame.Body, ref offset));
            string? pin = null;
            if (frame.Op == PinnedHello)
            {
                if (offset >= frame.Body.Length || frame.Body.Length - offset - 1 != frame.Body[offset])
                {
                    answers.Add(new Outgoing(Error(frame.Id, new Validation("pinned hello must end with a bounded region", "invalid_frame"))));
                    return null;
                }

                pin = Encoding.UTF8.GetString(frame.Body.AsSpan(offset + 1));
                if (!TerraBaseOptions.IsRegion(pin))
                {
                    answers.Add(new Outgoing(Error(frame.Id, new Unavailable($"use one valid region pin; see {RegionGuidance.Docs}", "misdirected_request"))));
                    return null;
                }
            }

            var access = await AuthorizeAsync(secret, project, database, pin, timeout.Token).ConfigureAwait(false);
            if (access is not Grant grant)
            {
                answers.Add(new Outgoing(Error(frame.Id, Refusal(access))));
                return null;
            }

            answers.Add(new Outgoing(Answer(frame.Id, 200, [Version], [])));
            await answers.FlushAsync().ConfigureAwait(false);
            return new Session(secret, project, database, pin) { Grant = grant };
        }
    }

    // The checks of an API-key request on the client listener: the key and its scope, the database in the key's reach, and
    // its placement in this region. The catalog commit sequence is taken first, so a commit racing the reads re-runs them.
    private async ValueTask<Access> AuthorizeAsync(string secret, string project, string database, string? pin, CancellationToken cancellationToken)
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

        var found = await services.GetRequiredService<IManagementService>().FindDatabaseRecordAsync(key.OrganizationId, project, name.Value, cancellationToken).ConfigureAwait(false);
        if (found is not ManagedDatabase record || !key.Reaches(new TenantContext(key.OrganizationId, PlanTier.Hobby, project, record.Id)))
        {
            return found is Unavailable unavailable ? unavailable : DocumentRoute.UnknownDatabase;
        }

        var region = services.GetRequiredService<TerraBaseOptions>().Region;
        if (!record.PlacedIn(region) || (pin is not null && pin != region))
        {
            var regions = await RegionGuidance.RegionsAsync(record, region, services.GetRequiredService<IRegionCatalog>(), cancellationToken).ConfigureAwait(false);
            var target = pin is not null && regions.Contains(pin, StringComparer.Ordinal) ? pin : regions[0];
            return new Unavailable(RegionGuidance.Message(target, regions), "wrong_region");
        }

        return await services.GetRequiredService<ITenantDirectory>().FindDatabaseAsync(key.OrganizationId, project, name.Value, cancellationToken).ConfigureAwait(false) switch
        {
            TenantContext tenant => new Grant(key, tenant, sequence),
            Unavailable error => error,
            _ => DocumentRoute.UnknownDatabase,
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

        var access = await AuthorizeAsync(session.Secret, session.Project, session.Database, session.Pin, cancellationToken).ConfigureAwait(false);
        if (access is Grant fresh && fresh.Database.DatabaseId == grant.Database.DatabaseId)
        {
            session.Grant = fresh;
            return fresh;
        }

        // A deleted and recreated database is another database: the session stays bound to the one it authorized.
        return access is Grant ? DocumentRoute.UnknownDatabase : access;
    }

    private async Task RequestAsync(
        Session session, Frame frame, Task prior, SemaphoreSlim slots, Outbox outbox, CancellationTokenSource closing, CancellationToken closed)
    {
        byte[]? answer = null;
        IMemoryReservation? held = null;
        try
        {
            if (!prior.IsCompleted)
            {
                await prior.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

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

            if (answer is null)
            {
                Release();
            }
            else
            {
                outbox.Add(new Outgoing(answer, Release));
            }
        }

        // An answer completed off the reading loop is flushed here; the loop flushes its own before it waits.
        if (!outbox.Deferring)
        {
            await outbox.FlushAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<byte[]> ExecuteAsync(Session session, Frame frame, CancellationTokenSource closing, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        TenantContext? tenant = null;
        byte[]? answer = null;
        try
        {
            if (!services.GetRequiredService<TerraBase.Features.Health.IPeerTopology>().IsCurrent)
            {
                return answer = Error(frame.Id, new Unavailable("this machine's peer topology is not verified", "peer_topology_mismatch"));
            }

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
                return answer = Error(frame.Id, new Validation("unknown operation", "unknown_operation"));
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
            return answer = Error(frame.Id, InternalError);
        }
        finally
        {
            // Metered like the HTTP request it replaces: one request with its bytes, on top of the document operation itself.
            if (tenant is not null && answer is not null)
            {
                var elapsed = Stopwatch.GetElapsedTime(started);
                var status = BinaryPrimitives.ReadUInt16LittleEndian(answer.AsSpan(8));
                services.GetRequiredService<IUsageMeter>().RecordRequest(tenant, frame.Op switch { Get => "GET", Put => "PUT", _ => "OTHER" }, status, elapsed, frame.Body.Length, answer.Length - 10);
                services.GetRequiredService<IPressureSignals>().Record(TerraBaseListener.Client, elapsed, tenant.OrganizationId, tenant.Plan);
            }
        }
    }

    // Body: u8 collection length, the collection, the document's 26-character ULID, then (put only) u8 durability and the JSON document.
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

        var (collection, id) = (checkedCollection, checkedId) is (string checkedName, string checkedKey) ? (checkedName, checkedKey) : throw new UnreachableException();

        // The node's own copy here; the document service refuses a faulted copy of this database on every read and write.
        if (await services.GetRequiredService<IReplicaCopyFaults>().RefusedAsync(ChangeGroups.System, Ulid.Empty, cancellationToken).ConfigureAwait(false))
        {
            throw new ReplicaCopyFaultedException("this database copy is awaiting certified recovery");
        }

        var documents = services.GetRequiredService<IDocumentService>();
        if (frame.Op == Get)
        {
            var key = new DocumentKey(tenant, collection, id);
            return await documents.GetAsync(key, cancellationToken).ConfigureAwait(false) switch
            {
                StoredDocument { UniqueConflict: true } => Error(frame.Id, new Conflict("the document lost a replicated uniqueness conflict", "unique_violation")),
                StoredDocument { Deleted: false } document => Read(frame.Id, ETags.Version(document.Scope, collection, document.Id, document.Version, document.Json), document.Json.Span),
                StoredDocument or NeverWritten => Error(frame.Id, new NotFound("the document does not exist")),
                NotFound error => Error(frame.Id, error),
                Validation error => Error(frame.Id, error),
                Forbidden error => Error(frame.Id, error),
            };
        }

        // After the id: u8 durability, as the HTTP API's TB-Durability (0 local_durable, 1 remote_durable), then the document.
        if (body.Length < 1 + length + 27 || body[1 + length + 26] > 1)
        {
            return Error(frame.Id, new Validation($"a put carries its durability: 0 {WriteResult.LocalDurable} or 1 {WriteResult.RemoteDurable}"));
        }

        var waitForRemote = body[1 + length + 26] == 1;
        var json = body.AsMemory(1 + length + 27).ToArray();
        if (!RequestJson.IsText(json))
        {
            return Error(frame.Id, new Validation(RequestJson.TextRule));
        }

        var none = new Precondition(null, IfMatchAny: false, IfNoneMatch: false);
        return await documents.WriteConfirmedAsync(tenant, [new PutOperation(collection, id, json, none)], indexErrors: false, waitForRemote, cancellationToken)
            .ConfigureAwait(false) switch
        {
            DocumentRejected rejected => Error(frame.Id, rejected.Error),
            WriteResult written => Answer(
                frame.Id, written.Results[0].Created ? (ushort)201 : (ushort)200,
                [(byte)(written.Durability == WriteResult.RemoteDurable ? 1 : 0), .. Short(written.Results[0].ETag!)], []),
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

        // Held until the request's answer is written.
        var reservation = services.GetRequiredService<IMemoryBudget>().TryReserve(MemoryLane.Foreground, 16 * 1024 + length * 3, tenant)
            ?? throw new QosMemoryException();
        try
        {
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
            return new Frame(BinaryPrimitives.ReadUInt32LittleEndian(head), head[4], body, reservation);
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A connection's answers in completion order: written and flushed by one caller at a time, then released. Once the client
    /// is gone, answers are released unwritten.
    /// </summary>
    private sealed class Outbox(PipeWriter output, CancellationToken closed)
    {
        private readonly Queue<Outgoing> _queue = new();
        private readonly List<Outgoing> _batch = [];
        private int _flushing;
        private bool _gone;

        /// <summary>Set while the reading loop runs a request inline: its answer waits for the loop's next flush.</summary>
        public bool Deferring
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        public void Add(Outgoing answer)
        {
            lock (_queue)
            {
                _queue.Enqueue(answer);
            }
        }

        public async ValueTask FlushAsync()
        {
            // Whoever holds the gate writes what was added before it rechecks the queue, so an answer is never stranded.
            while (Interlocked.CompareExchange(ref _flushing, 1, 0) == 0)
            {
                try
                {
                    lock (_queue)
                    {
                        while (_queue.TryDequeue(out var answer))
                        {
                            _batch.Add(answer);
                        }
                    }

                    if (_batch.Count > 0)
                    {
                        await WriteAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    Volatile.Write(ref _flushing, 0);
                }

                lock (_queue)
                {
                    if (_queue.Count == 0)
                    {
                        return;
                    }
                }
            }
        }

        private async ValueTask WriteAsync()
        {
            try
            {
                if (!_gone)
                {
                    foreach (var answer in _batch)
                    {
                        output.Write(answer.Answer);
                    }

                    _gone = (await output.FlushAsync(closed).ConfigureAwait(false)).IsCompleted;
                }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException)
            {
                _gone = true;
            }
            finally
            {
                foreach (var answer in _batch)
                {
                    answer.Release?.Invoke();
                }

                _batch.Clear();
            }
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

    // A get's answer: 200, the u8-length ETag, then the document, written into one array.
    private static byte[] Read(uint id, string etag, ReadOnlySpan<byte> json)
    {
        var answer = new byte[11 + etag.Length + json.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(answer, (uint)(answer.Length - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(answer.AsSpan(4), id);
        BinaryPrimitives.WriteUInt16LittleEndian(answer.AsSpan(8), 200);
        answer[10] = checked((byte)etag.Length);
        Encoding.ASCII.GetBytes(etag, answer.AsSpan(11));
        json.CopyTo(answer.AsSpan(11 + etag.Length));
        return answer;
    }

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
            Unavailable { Code: "wrong_region" or "misdirected_request" } e => (421, e.Code, e.Message),
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
