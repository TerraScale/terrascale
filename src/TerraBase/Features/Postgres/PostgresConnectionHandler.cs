using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Connections;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management.Queries;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Postgres;

/// <summary>
/// PostgreSQL wire protocol 3.0 on the postgres listener: plaintext behind the platform's TLS edge, a TerraBase API key as
/// the password, and the SQL subset through <see cref="IQueryService"/> under the tenant's QoS admission.
/// </summary>
internal sealed class PostgresConnectionHandler(IServiceProvider services) : ConnectionHandler
{
    private const int Protocol30 = 196608, CancelCode = 80877102, SslCode = 80877103, GssCode = 80877104;
    private const int MaxStartupBytes = 10_000, MaxMessageBytes = 1024 * 1024, MaxNamed = 256;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly PgError Denied = new("28P01", "password authentication failed");

    // Sessions by process id, for CancelRequest; one handler serves the whole listener.
    private readonly ConcurrentDictionary<int, Session> _sessions = new();

    private readonly union Access(TenantContext, PgError);

    // Resolved per use, not when Kestrel starts: opening the engine there would bypass Program's startup error reporting.
    private IApiKeyStore Keys => services.GetRequiredService<IApiKeyStore>();

    private ITenantDirectory Tenants => services.GetRequiredService<ITenantDirectory>();

    private IMemoryBudget Memory => services.GetRequiredService<IMemoryBudget>();

    /// <inheritdoc />
    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var input = connection.Transport.Input;
        var output = new PgWriter(connection.Transport.Output);
        Session? session = null;
        try
        {
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed))
            {
                handshake.CancelAfter(HandshakeTimeout);
                session = await StartAsync(input, output, handshake.Token).ConfigureAwait(false);
            }

            if (session is not null)
            {
                await ServeAsync(session, input, output, connection.ConnectionClosed).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is InvalidDataException or QosMemoryException)
        {
            // Framing errors leave the stream unreadable and memory refusals leave a body unread: both end the session.
            output.Error(error is QosMemoryException ? "53200" : "08P01", error.Message, fatal: true);
            await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException)
        {
            // Closed by the client, the shutdown drain or the handshake timeout.
        }
        finally
        {
            if (session is not null)
            {
                _sessions.TryRemove(session.ProcessId, out _);
            }
        }
    }

    private async ValueTask<Session?> StartAsync(PipeReader input, PgWriter output, CancellationToken cancellationToken)
    {
        for (var negotiated = 0; ; negotiated++)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(await ReadAsync(input, 4, cancellationToken).ConfigureAwait(false));
            if (length is < 8 or > MaxStartupBytes)
            {
                throw new InvalidDataException("invalid startup packet length");
            }

            var packet = new PgReader(await ReadAsync(input, length - 4, cancellationToken).ConfigureAwait(false));
            var code = packet.Int32();
            if (code is SslCode or GssCode && negotiated < 2)
            {
                // TLS ends at the platform edge: this listener speaks plaintext, and the client continues on this connection.
                output.Raw((byte)'N');
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (code == CancelCode)
            {
                var (processId, secret) = (packet.Int32(), packet.Int32());
                if (_sessions.TryGetValue(processId, out var target) && target.Secret == secret)
                {
                    target.Cancel();
                }

                return null;
            }

            if (code >> 16 != 3)
            {
                output.Error("0A000", "unsupported frontend protocol: TerraBase speaks protocol 3.0", fatal: true);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var name = packet.CString(); name.Length > 0; name = packet.CString())
            {
                parameters[name] = packet.CString();
            }

            if (code != Protocol30)
            {
                var unknown = parameters.Keys.Where(name => name.StartsWith("_pq_.", StringComparison.Ordinal)).ToList();
                output.Begin('v');
                output.Int32(Protocol30);
                output.Int32(unknown.Count);
                unknown.ForEach(output.CString);
                output.End();
            }

            return await AuthenticateAsync(input, output, parameters, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<Session?> AuthenticateAsync(PipeReader input, PgWriter output, Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        output.Begin('R');
        output.Int32(3); // AuthenticationCleartextPassword: the password is an API key.
        output.End();
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        var (type, body, reservation) = await ReadMessageAsync(input, cancellationToken).ConfigureAwait(false);
        using (reservation)
        {
            if (type != 'p')
            {
                throw new InvalidDataException("expected a password message");
            }

            var secret = new PgReader(body).CString();
            var database = parameters.GetValueOrDefault("database") ?? parameters.GetValueOrDefault("user") ?? "";
            if (await AuthorizeAsync(secret, database, cancellationToken).ConfigureAwait(false) is PgError error)
            {
                output.Error(error.Code, error.Message, fatal: true);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            Session session;
            do
            {
                session = new Session(RandomNumberGenerator.GetInt32(1, int.MaxValue), RandomNumberGenerator.GetInt32(int.MaxValue), secret, database,
                    PostgresStatements.Settings(parameters.GetValueOrDefault("application_name") ?? ""));
            }
            while (!_sessions.TryAdd(session.ProcessId, session));

            output.Begin('R');
            output.Int32(0);
            output.End();
            foreach (var name in PostgresStatements.Reported)
            {
                output.Begin('S');
                output.CString(name);
                output.CString(session.Settings[name]);
                output.End();
            }

            output.Begin('K');
            output.Int32(session.ProcessId);
            output.Int32(session.Secret);
            output.End();
            output.Ready();
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
    }

    // Every statement re-checks the key and database, like every HTTP request: revocation and deletion apply at once.
    // `database` is a name in the key's project, or project/name for a key that reaches the whole organization.
    private async ValueTask<Access> AuthorizeAsync(string secret, string database, CancellationToken cancellationToken)
    {
        var slash = database.IndexOf('/', StringComparison.Ordinal);
        if (await Keys.FindAsync(secret, cancellationToken).ConfigureAwait(false) is not ApiKey key
            || !key.Scopes.Contains(ApiKeyScopes.DocumentsRead)
            || (slash < 0 ? key.ProjectId : database[..slash]) is not { } project
            || !EntityIds.IsValid(project)
            || !DatabaseName.TryParse(database[(slash + 1)..], out var name))
        {
            return Denied;
        }

        return await Tenants.FindDatabaseAsync(key.OrganizationId, project, name.Value, cancellationToken).ConfigureAwait(false) switch
        {
            TenantContext tenant when key.Reaches(tenant) => tenant,
            Unavailable => new PgError("57P03", "the database is unavailable; retry"),
            _ => Denied,
        };
    }

    private async Task ServeAsync(Session session, PipeReader input, PgWriter output, CancellationToken cancellationToken)
    {
        // After an error in the extended protocol, messages are discarded until Sync.
        var failed = false;
        while (true)
        {
            var (type, body, reservation) = await ReadMessageAsync(input, cancellationToken).ConfigureAwait(false);
            using (reservation)
            {
                if (type == 'X')
                {
                    return;
                }

                var message = new PgReader(body);
                switch ((char)type)
                {
                    case 'Q':
                        var sql = message.CString();
                        var outcome = PgCatalog.IsTypeLoad(sql) ? PgCatalog.TypeLoad() : await ExecuteAsync(session, sql, cancellationToken).ConfigureAwait(false);
                        if (outcome is PgResult[] results)
                        {
                            foreach (var result in results)
                            {
                                Write(output, result, [], describe: true);
                            }
                        }
                        else if (outcome is PgError error)
                        {
                            output.Error(error.Code, error.Message);
                        }

                        output.Ready();
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                        failed = false;
                        break;
                    case 'S':
                        failed = false;
                        output.Ready();
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case 'H':
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case 'P' or 'B' or 'D' or 'E' or 'C' when !failed:
                        if (await ExtendedAsync(session, (char)type, message, output, cancellationToken).ConfigureAwait(false) is { } refused)
                        {
                            output.Error(refused.Code, refused.Message);
                            failed = true;
                        }

                        break;
                    case 'P' or 'B' or 'D' or 'E' or 'C':
                        break;
                    default:
                        throw new InvalidDataException("unsupported message type");
                }
            }
        }
    }

    private async ValueTask<PgError?> ExtendedAsync(Session session, char type, PgReader message, PgWriter output, CancellationToken cancellationToken)
    {
        switch (type)
        {
            case 'P':
                var (statementName, sql) = (message.CString(), message.CString());
                var oids = Enumerable.Range(0, message.Count()).Select(_ => message.Int32()).ToList();
                var count = PostgresStatements.MaxParameter(sql);
                if (count < 0)
                {
                    return new PgError("42P02", "parameter numbers must be $1 to $32767");
                }

                if (statementName.Length > 0 && session.Statements.ContainsKey(statementName))
                {
                    return new PgError("42P05", "prepared statement already exists");
                }

                if (session.Statements.Count >= MaxNamed && !session.Statements.ContainsKey(statementName))
                {
                    return new PgError("54000", "at most 256 prepared statements per session");
                }

                oids.AddRange(Enumerable.Repeat(0, Math.Max(0, count - oids.Count)));
                session.Statements[statementName] = new Prepared(sql, [.. oids]);
                output.Empty('1');
                return null;
            case 'B':
                var (portalName, boundName) = (message.CString(), message.CString());
                var formats = Enumerable.Range(0, message.Count()).Select(_ => message.Int16()).ToArray();
                var values = Enumerable.Range(0, message.Count()).Select(_ => message.Bytes()).ToArray();
                var resultFormats = Enumerable.Range(0, message.Count()).Select(_ => message.Int16()).ToArray();
                if (!session.Statements.TryGetValue(boundName, out var bound))
                {
                    return new PgError("26000", "prepared statement does not exist");
                }

                if (values.Length != bound.Oids.Length || formats.Length > 1 && formats.Length != values.Length)
                {
                    return new PgError("08P01", $"bind message supplies {values.Length} parameters, but prepared statement requires {bound.Oids.Length}");
                }

                var literals = new string[values.Length];
                for (var i = 0; i < values.Length; i++)
                {
                    if (PostgresStatements.Literal(bound.Oids[i], formats.Length == 0 ? 0 : formats[Math.Min(i, formats.Length - 1)], values[i]) is not { } literal)
                    {
                        return new PgError("22P02", $"invalid value for parameter ${i + 1}, or an unsupported binary type");
                    }

                    literals[i] = literal;
                }

                if (session.Portals.Count >= MaxNamed && !session.Portals.ContainsKey(portalName))
                {
                    return new PgError("54000", "at most 256 portals per session");
                }

                session.Portals[portalName] = new Portal(PostgresStatements.Substitute(bound.Sql, literals), resultFormats);
                output.Empty('2');
                return null;
            case 'D':
                var (kind, described) = (message.Byte(), message.CString());
                if (kind == 'S' && session.Statements.TryGetValue(described, out var statement))
                {
                    output.Begin('t');
                    output.Int16(statement.Oids.Length);
                    Array.ForEach(statement.Oids, oid => output.Int32(oid == 0 ? PostgresStatements.TextOid : oid));
                    output.End();
                    Describe(output, PostgresStatements.Describe(statement.Sql), []);
                    return null;
                }

                if (kind == 'P' && session.Portals.TryGetValue(described, out var portal))
                {
                    Describe(output, PostgresStatements.Describe(portal.Sql), portal.Formats);
                    return null;
                }

                return new PgError(kind == 'S' ? "26000" : "34000", "prepared statement or portal does not exist");
            case 'E':
                var (executed, maxRows) = (message.CString(), message.Int32());
                if (!session.Portals.TryGetValue(executed, out var running))
                {
                    return new PgError("34000", "portal does not exist");
                }

                if (maxRows > 0)
                {
                    return new PgError("0A000", "Execute with a row limit is not supported yet");
                }

                var outcome = await ExecuteAsync(session, running.Sql, cancellationToken).ConfigureAwait(false);
                if (outcome is PgError error)
                {
                    return error;
                }

                foreach (var result in outcome is PgResult[] results ? results : [])
                {
                    Write(output, result, running.Formats, describe: false);
                }

                return null;
            default:
                var (closed, closedName) = (message.Byte(), message.CString());
                _ = closed == 'S' ? session.Statements.Remove(closedName) : session.Portals.Remove(closedName);
                output.Empty('3');
                return null;
        }
    }

    private async ValueTask<PgOutcome> ExecuteAsync(Session session, string sql, CancellationToken cancellationToken)
    {
        var kind = PostgresStatements.Classify(sql, out var text);
        if (kind == PgKind.Discard)
        {
            session.Statements.Clear();
            session.Portals.Clear();
        }

        if (kind != PgKind.Select)
        {
            return PostgresStatements.RunCommand(kind, text, session.Settings);
        }

        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        session.Track(running);
        try
        {
            if (await AuthorizeAsync(session.ApiKey, session.Database, running.Token).ConfigureAwait(false) is not TenantContext database)
            {
                return Denied;
            }

            // The HTTP pipeline's admission (QosExtensions): one organization lease, then the tenant's memory and budgets.
            var tenant = new TenantContext(database.OrganizationId, database.Plan);
            using var lease = services.GetRequiredService<IQosAdmission>().TryEnter(tenant, out var retryAfter);
            if (lease is null)
            {
                return new PgError("53300", $"tenant busy, retry after {retryAfter}s");
            }

            using var scope = QosContext.Enter(tenant, Memory);
            return SqlSubset.Parse(text) switch
            {
                Validation error => PostgresStatements.Error(error),
                SqlStatement statement => await services.GetRequiredService<IQueryService>().RunAsync(database, statement, running.Token).ConfigureAwait(false) switch
                {
                    SqlResultBody body => new[] { PostgresStatements.Rows(statement, body) },
                    Validation error => PostgresStatements.Error(error),
                },
            };
        }
        catch (OperationCanceledException) when (running.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new PgError("57014", "canceling statement due to user request");
        }
        catch (QosMemoryException)
        {
            return new PgError("53200", "organization memory budget exceeded");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            services.GetRequiredService<ILogger<PostgresConnectionHandler>>().ZLogError(error, $"A PostgreSQL wire statement failed");
            return new PgError("XX000", "internal error");
        }
        finally
        {
            session.Track(null);
        }
    }

    private static void Describe(PgWriter output, PgColumn[]? columns, short[] formats)
    {
        if (columns is null)
        {
            output.Empty('n');
            return;
        }

        output.Begin('T');
        output.Int16(columns.Length);
        for (var i = 0; i < columns.Length; i++)
        {
            output.CString(columns[i].Name);
            output.Int32(0);
            output.Int16(0);
            output.Int32(columns[i].Oid);
            output.Int16(-1);
            output.Int32(-1);
            // Text, json and the session values send the same bytes in text and binary format.
            output.Int16(formats.Length == 0 ? 0 : formats[Math.Min(i, formats.Length - 1)]);
        }

        output.End();
    }

    private static void Write(PgWriter output, PgResult result, short[] formats, bool describe)
    {
        if (result.Tag is null)
        {
            output.Empty('I');
            return;
        }

        if (describe && result.Columns is not null)
        {
            Describe(output, result.Columns, formats);
        }

        foreach (var row in result.Rows)
        {
            output.Begin('D');
            output.Int16(row.Length);
            foreach (var value in row)
            {
                output.Value(value);
            }

            output.End();
        }

        if (result.Notice is not null)
        {
            output.Notice(result.Notice);
        }

        output.Begin('C');
        output.CString(result.Tag);
        output.End();
    }

    private async ValueTask<(byte Type, byte[] Body, IMemoryReservation Reservation)> ReadMessageAsync(PipeReader input, CancellationToken cancellationToken)
    {
        var header = await ReadAsync(input, 5, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        if (length is < 4 or > MaxMessageBytes)
        {
            // Before allocating: the unread body cannot be skipped safely.
            throw new InvalidDataException("message length out of bounds");
        }

        var reservation = Memory.TryReserve(MemoryLane.Foreground, length) ?? throw new QosMemoryException();
        try
        {
            return (header[0], await ReadAsync(input, length - 4, cancellationToken).ConfigureAwait(false), reservation);
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
    }

    private static async ValueTask<byte[]> ReadAsync(PipeReader input, int count, CancellationToken cancellationToken)
    {
        if (count == 0)
        {
            return [];
        }

        var read = await input.ReadAtLeastAsync(count, cancellationToken).ConfigureAwait(false);
        if (read.Buffer.Length < count)
        {
            input.AdvanceTo(read.Buffer.Start, read.Buffer.End);
            throw new EndOfStreamException();
        }

        var bytes = read.Buffer.Slice(0, count).ToArray();
        input.AdvanceTo(read.Buffer.GetPosition(count));
        return bytes;
    }

    private sealed record Prepared(string Sql, int[] Oids);

    private sealed record Portal(string Sql, short[] Formats);

    // The running statement's cancellation, swapped under a lock so CancelRequest never cancels a disposed source.
    private sealed record Session(int ProcessId, int Secret, string ApiKey, string Database, Dictionary<string, string> Settings)
    {
        private readonly Lock _gate = new();
        private CancellationTokenSource? _running;

        public Dictionary<string, Prepared> Statements { get; } = [];

        public Dictionary<string, Portal> Portals { get; } = [];

        public void Track(CancellationTokenSource? running)
        {
            lock (_gate)
            {
                _running = running;
            }
        }

        public void Cancel()
        {
            lock (_gate)
            {
                _running?.Cancel();
            }
        }
    }

    // Reads a frontend message body; running past its end is a framing error.
    private sealed class PgReader(byte[] data)
    {
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private int _position;

        public byte Byte() => Take(1)[0];

        public short Int16() => BinaryPrimitives.ReadInt16BigEndian(Take(2));

        public int Int32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));

        public int Count() => Int16() is >= 0 and var count ? count : throw new InvalidDataException("negative count");

        public byte[]? Bytes() => Int32() is var length and >= 0 ? Take(length).ToArray() : null;

        public string CString()
        {
            var end = Array.IndexOf(data, (byte)0, _position);
            if (end < 0)
            {
                throw new InvalidDataException("unterminated string");
            }

            try
            {
                return Utf8.GetString(Take(end - _position + 1)[..^1]);
            }
            catch (DecoderFallbackException)
            {
                throw new InvalidDataException("invalid UTF-8");
            }
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > data.Length - _position)
            {
                throw new InvalidDataException("message shorter than its contents");
            }

            _position += count;
            return data.AsSpan(_position - count, count);
        }
    }

    // Builds backend messages: type, big-endian length, body.
    private sealed class PgWriter(PipeWriter pipe)
    {
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private readonly ArrayBufferWriter<byte> _body = new();
        private byte _type;

        public void Begin(char type)
        {
            _type = (byte)type;
            _body.ResetWrittenCount();
        }

        public void Int16(int value)
        {
            BinaryPrimitives.WriteInt16BigEndian(_body.GetSpan(2), (short)value);
            _body.Advance(2);
        }

        public void Int32(int value)
        {
            BinaryPrimitives.WriteInt32BigEndian(_body.GetSpan(4), value);
            _body.Advance(4);
        }

        public void CString(string value)
        {
            Utf8.GetBytes(value, _body);
            _body.Write([(byte)0]);
        }

        public void Value(string? value)
        {
            if (value is null)
            {
                Int32(-1);
                return;
            }

            Int32(Utf8.GetByteCount(value));
            Utf8.GetBytes(value, _body);
        }

        public void End()
        {
            var length = _body.WrittenCount;
            var span = pipe.GetSpan(length + 5);
            span[0] = _type;
            BinaryPrimitives.WriteInt32BigEndian(span[1..], length + 4);
            _body.WrittenSpan.CopyTo(span[5..]);
            pipe.Advance(length + 5);
        }

        public void Empty(char type)
        {
            Begin(type);
            End();
        }

        public void Raw(byte value)
        {
            pipe.GetSpan(1)[0] = value;
            pipe.Advance(1);
        }

        public void Ready()
        {
            Begin('Z');
            _body.Write([(byte)'I']);
            End();
        }

        public void Error(string code, string message, bool fatal = false) => Fields('E', fatal ? "FATAL" : "ERROR", code, message);

        public void Notice(string message) => Fields('N', "WARNING", "01000", message);

        public ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken) => pipe.FlushAsync(cancellationToken);

        private void Fields(char type, string severity, string code, string message)
        {
            Begin(type);
            foreach (var (field, value) in new[] { ('S', severity), ('V', severity), ('C', code), ('M', message) })
            {
                _body.Write([(byte)field]);
                CString(value);
            }

            _body.Write([(byte)0]);
            End();
        }
    }
}
