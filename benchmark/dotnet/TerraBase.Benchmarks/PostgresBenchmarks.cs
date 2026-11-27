using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraBase.Postgres.Hosting;
using TerraBase.Postgres.Protocol;
using TerraBase.Postgres.Sql;
using TerraBase.Storage;
using TerraScale.Contracts.Cluster;
using TerraScale.Contracts.Consistency;
using TerraScale.Contracts.Continuity;
using TerraScale.Contracts.Storage;

namespace TerraBase.Benchmarks;

public static class PostgresBenchmarks
{
    private const string Suite = "postgres";

    private const string BenchPassword = "tb_postgres_loopback_bench_secret";

    private static readonly (string Name, string Sql, int ExpectedParameters)[] RewriteCases =
    [
        (
            "select",
            "SELECT u.id::bigint, u.email, u.created_at::timestamptz FROM public.users u WHERE u.tenant_id = $1::uuid AND u.email ILIKE $2 AND u.role = ANY($3) ORDER BY u.id DESC LIMIT 50",
            3
        ),
        (
            "insert",
            "INSERT INTO public.orders (id, user_id, amount, payload, created_at) VALUES ($1::bigint, $2::uuid, $3::numeric, $4::jsonb, NOW()) RETURNING id, created_at",
            4
        ),
        (
            "update",
            "UPDATE public.accounts SET balance = balance + $1::numeric, updated_at = CURRENT_TIMESTAMP WHERE id = $2::bigint AND active = 'true'::boolean RETURNING balance",
            2
        ),
        (
            "cte",
            "WITH active_users AS (SELECT id, org_id FROM public.users WHERE active = 't'::bool AND deleted_at IS NULL) SELECT o.id, o.total::float8 FROM public.orders o JOIN active_users au ON au.id = o.user_id WHERE o.status IS DISTINCT FROM $1::text LIMIT 25",
            1
        ),
    ];

    private static readonly byte[] SampleBlob =
    [
        0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77,
        0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF,
        0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80,
        0x90, 0xA0, 0xB0, 0xC0, 0xD0, 0xE0, 0xF0, 0x0F,
    ];

    private static readonly (int Oid, SqlValue Value)[] CodecCases =
    [
        (PgOid.Bool, SqlValue.FromIntegral(1)),
        (PgOid.Int2, SqlValue.FromIntegral(32000)),
        (PgOid.Int4, SqlValue.FromIntegral(123456789)),
        (PgOid.Int8, SqlValue.FromIntegral(9_000_000_000_123L)),
        (PgOid.Float8, SqlValue.FromReal(3.14159265358979)),
        (PgOid.Numeric, SqlValue.FromText("12345678.9012")),
        (PgOid.Text, SqlValue.FromText("terrabase-postgres-wire-payload-value")),
        (PgOid.Uuid, SqlValue.FromText("550e8400-e29b-41d4-a716-446655440000")),
        (PgOid.Timestamptz, SqlValue.FromText("2026-11-26 21:30:00.123456+00")),
        (PgOid.Bytea, SqlValue.FromBlob(SampleBlob)),
        (PgOid.Jsonb, SqlValue.FromText("{\"ok\":true,\"count\":42}")),
    ];

    public static async Task RunAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        RunLexerAndRewriter(ctx);
        RunValueCodec(ctx);
        await RunWireLoopbackAsync(ctx, cancellationToken);
    }

    private static void RunLexerAndRewriter(BenchRunnerContext ctx)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["standard_conforming_strings"] = "on",
            ["TimeZone"] = "UTC",
        };
        var rewriteContext = new PgRewriteContext("benchdb", "bench", 4242, settings);

        ctx.MeasureSync(
            Suite,
            "lexer_and_rewriter",
            warmup: 150,
            iterations: 1500,
            iteration =>
            {
                _ = iteration;
                foreach (var (name, sql, expectedParameters) in RewriteCases)
                {
                    var tokens = RequireTokens(PgLexer.Tokenize(sql), name);
                    var editor = PgTokenEditor.Create(sql, tokens);
                    _ = RequireRewrite(PostgresSqlRewriter.Rewrite(editor, rewriteContext), name, expectedParameters);
                }
            },
            note: "4 statements/iter: SELECT+ILIKE+ANY, INSERT+NOW+RETURNING, UPDATE+casts, CTE+IS DISTINCT FROM");
    }

    private static PgToken[] RequireTokens(PgTokenizeResult tokenized, string name)
    {
        var (tokens, lexError) = tokenized switch
        {
            PgToken[] parsed => (parsed, (string?)null),
            PgError error => ((PgToken[]?)null, error.Message),
        };
        if (tokens is null || tokens.Length == 0)
        {
            throw new InvalidOperationException($"PgLexer failed for {name}: {lexError ?? "empty tokens"}");
        }

        return tokens;
    }

    private static PgRewrite RequireRewrite(PgRewriteResult rewritten, string name, int expectedParameters)
    {
        var (result, rewriteError) = rewritten switch
        {
            PgRewrite ok => (ok, (string?)null),
            PgError error => ((PgRewrite?)null, error.Message),
        };
        if (result is null || string.IsNullOrWhiteSpace(result.Sql) || result.ParameterCount != expectedParameters)
        {
            throw new InvalidOperationException($"PostgresSqlRewriter failed for {name}: {rewriteError ?? "unexpected parameter count"}");
        }

        return result;
    }

    private static void RunValueCodec(BenchRunnerContext ctx)
    {
        using var buffer = new PgMessageBuffer();

        ctx.MeasureSync(
            Suite,
            "value_codec_text_and_binary",
            warmup: 200,
            iterations: 2000,
            _ =>
            {
                foreach (var (oid, value) in CodecCases)
                {
                    VerifyCodecRoundTrip(buffer, oid, value, binary: false);
                    VerifyCodecRoundTrip(buffer, oid, value, binary: true);
                }
            },
            note: "11 Postgres OIDs x text+binary encode/decode round-trip/iter");
    }

    private static void VerifyCodecRoundTrip(PgMessageBuffer buffer, int oid, SqlValue value, bool binary)
    {
        buffer.Clear();
        if (!PgValueCodec.TryWrite(buffer, value, oid, binary))
        {
            throw new InvalidOperationException($"PgValueCodec.TryWrite failed for OID {oid.ToString(CultureInfo.InvariantCulture)} (binary={binary})");
        }

        if (buffer.Length < 4)
        {
            throw new InvalidOperationException($"PgValueCodec wrote truncated frame for OID {oid.ToString(CultureInfo.InvariantCulture)}");
        }

        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(buffer.WrittenSpan[..4]);
        if (payloadLength < 0 || buffer.Length != payloadLength + 4)
        {
            throw new InvalidOperationException($"PgValueCodec length mismatch for OID {oid.ToString(CultureInfo.InvariantCulture)}");
        }

        var raw = buffer.WrittenSpan.Slice(4, payloadLength).ToArray();
        short format = binary ? (short)1 : (short)0;
        if (!PgValueCodec.TryDecode(oid, format, raw, out var decoded) || decoded.Kind == SqlValueKind.Null)
        {
            throw new InvalidOperationException($"PgValueCodec.TryDecode failed for OID {oid.ToString(CultureInfo.InvariantCulture)} (binary={binary})");
        }

        if (oid is PgOid.Bool or PgOid.Int2 or PgOid.Int4 or PgOid.Int8)
        {
            if (!value.TryGetIntegral(out var expected) || !decoded.TryGetIntegral(out var actual) || actual != expected)
            {
                throw new InvalidOperationException($"Integral round-trip mismatch for OID {oid.ToString(CultureInfo.InvariantCulture)}");
            }
        }
        else if (oid is PgOid.Text or PgOid.Uuid or PgOid.Jsonb)
        {
            if (!value.TryGetText(out var expected) || !decoded.TryGetText(out var actual) || !string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Text round-trip mismatch for OID {oid.ToString(CultureInfo.InvariantCulture)}");
            }
        }
        else if (oid == PgOid.Bytea)
        {
            if (!value.TryGetBlob(out var expected) || !decoded.TryGetBlob(out var actual) || !actual.Span.SequenceEqual(expected.Span))
            {
                throw new InvalidOperationException("Bytea round-trip mismatch");
            }
        }
        else if (oid == PgOid.Float8)
        {
            if (!value.TryGetReal(out var expected) || !decoded.TryGetReal(out var actual) || Math.Abs(actual - expected) > 1e-12)
            {
                throw new InvalidOperationException("Float8 round-trip mismatch");
            }
        }
    }

    private static async Task RunWireLoopbackAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        var runDir = ctx.RunDir("postgres_wire");
        var storageOptions = new StorageEngineOptions(runDir, "local", 1, 1, new DeltaColumnRegistry())
        {
            ReaderCount = 4,
            BookmarkWaitTimeout = TimeSpan.FromSeconds(5),
            StatementTimeout = TimeSpan.FromSeconds(5),
        };
        await using var engine = new SqliteStorageEngine(storageOptions, TimeProvider.System);
        var databaseId = Ulid.NewUlid(TimeProvider.System.GetUtcNow());
        var rawKey = RandomNumberGenerator.GetBytes(DatabaseOpenRequest.RawKeyLength);
        var created = await engine.CreateAsync(new DatabaseCreateRequest(databaseId, rawKey, Strict: false), cancellationToken);
        var (session, createError) = created switch
        {
            ISqlDatabaseSession opened => (opened, (string?)null),
            DatabaseAlreadyExists exists => ((ISqlDatabaseSession?)null, exists.Message),
            InvalidDatabaseRequest invalid => (null, invalid.Message),
            StorageBusy busy => (null, busy.Message),
        };
        if (session is null)
        {
            throw new InvalidOperationException($"Failed to create SQLite database for Postgres benchmark: {createError}");
        }

        await using (session)
        {
            var executor = new DirectSqlitePostgresExecutor(session);
            var sessionFactory = new DirectPostgresSessionFactory(executor, BenchPassword);

            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseTerraBasePostgres();
            }));
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<IPostgresSessionFactory>(sessionFactory);
            builder.Services.AddTerraBasePostgres(new PostgresServerOptions(Tls: null));

            var app = builder.Build();
            await app.StartAsync(cancellationToken);
            try
            {
                var boundPort = ResolveBoundPort(app);
                await using var client = await PgWireLoopbackClient.ConnectAsync(boundPort, "bench", "proj/benchdb", BenchPassword, cancellationToken);

                var ddl = await client.SimpleQueryAsync(
                    "CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT NOT NULL, score INTEGER NOT NULL)",
                    cancellationToken);
                if (ddl.CommandTag != "CREATE TABLE")
                {
                    throw new InvalidOperationException($"Unexpected DDL tag: {ddl.CommandTag}");
                }

                for (var id = 1; id <= 200; id++)
                {
                    var seeded = await client.SimpleQueryAsync(
                        $"INSERT INTO items (id, name, score) VALUES ({id.ToString(CultureInfo.InvariantCulture)}, 'item-{id.ToString(CultureInfo.InvariantCulture)}', {(id * 10).ToString(CultureInfo.InvariantCulture)})",
                        cancellationToken);
                    if (seeded.CommandTag != "INSERT 0 1")
                    {
                        throw new InvalidOperationException($"Unexpected seed tag: {seeded.CommandTag}");
                    }
                }

                await ctx.MeasureAsync(
                    Suite,
                    "wire_simple_and_extended_loopback",
                    warmup: 25,
                    iterations: 250,
                    async iteration =>
                    {
                        var targetId = (iteration % 200) + 1;
                        var simple = await client.SimpleQueryAsync(
                            $"SELECT id, name, score FROM items WHERE id = {targetId.ToString(CultureInfo.InvariantCulture)}",
                            cancellationToken);
                        if (simple.RowCount != 1 || simple.CommandTag != "SELECT 1")
                        {
                            throw new InvalidOperationException($"Simple query returned {simple.RowCount.ToString(CultureInfo.InvariantCulture)} rows ({simple.CommandTag})");
                        }

                        var extendedSelect = await client.ExtendedSelectByIdAsync(targetId, cancellationToken);
                        if (extendedSelect.RowCount != 1 || extendedSelect.CommandTag != "SELECT 1")
                        {
                            throw new InvalidOperationException($"Extended SELECT returned {extendedSelect.RowCount.ToString(CultureInfo.InvariantCulture)} rows ({extendedSelect.CommandTag})");
                        }

                        var extendedUpdate = await client.ExtendedUpdateScoreAsync(targetId, (iteration % 1000) + 1, cancellationToken);
                        if (extendedUpdate.CommandTag != "UPDATE 1")
                        {
                            throw new InvalidOperationException($"Extended UPDATE returned {extendedUpdate.CommandTag}");
                        }
                    },
                    note: "TCP loopback Kestrel PostgresConnectionHandler: Simple SELECT + Extended Parse/Bind/Execute/Sync SELECT & UPDATE",
                    cancellationToken: cancellationToken);
            }
            finally
            {
                await app.StopAsync(CancellationToken.None);
                await app.DisposeAsync();
            }
        }
    }

    private static int ResolveBoundPort(WebApplication app)
    {
        var feature = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        if (feature?.Addresses.FirstOrDefault() is not { } address || !Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Failed to resolve Kestrel Postgres listener port");
        }

        return uri.Port;
    }

    private sealed class DirectPostgresSessionFactory(IPostgresDatabaseExecutor executor, string expectedPassword) : IPostgresSessionFactory
    {
        private readonly byte[] _expectedPasswordBytes = Encoding.UTF8.GetBytes(expectedPassword);

        public ValueTask<PostgresAuthenticateOutcome> AuthenticateAsync(PostgresStartup startup, CancellationToken cancellationToken)
        {
            var actual = Encoding.UTF8.GetBytes(startup.Password);
            if (!CryptographicOperations.FixedTimeEquals(actual, _expectedPasswordBytes))
            {
                return ValueTask.FromResult<PostgresAuthenticateOutcome>(new PostgresPasswordRejected("invalid password"));
            }

            var grant = new PostgresGrant(executor, startup.DatabaseReference, SqlAccess.Write, EmptyAsyncDisposable.Instance, CancellationToken.None, cancellationToken);
            return ValueTask.FromResult<PostgresAuthenticateOutcome>(grant);
        }
    }

    private sealed class EmptyAsyncDisposable : IAsyncDisposable
    {
        public static EmptyAsyncDisposable Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DirectSqlitePostgresExecutor(ISqlDatabaseSession session) : IPostgresDatabaseExecutor
    {
        public Ulid DatabaseId => session.DatabaseId;

        public CausalBookmark CurrentBookmark => session.CurrentBookmark;

        public ValueTask<SqlReadOutcome> ReadAsync(SqlStatementRequest request, CancellationToken cancellationToken) =>
            session.ExecuteReadAsync(request, cancellationToken);

        public async ValueTask<ClusterWriteOutcome> WriteAsync(SqlStatementRequest request, CancellationToken cancellationToken)
        {
            var outcome = await session.ExecuteWriteAsync(request, cancellationToken);
            return outcome switch
            {
                SqlWriteResult written => written,
                StrongRequired => await WriteStrongLocalAsync(request, cancellationToken),
                SqlError error => error,
                SchemaMismatch mismatch => mismatch,
                StorageBusy busy => busy,
                StorageTimeout timeout => new ClusterTimeout(timeout.Message, timeout.Code),
            };
        }

        private async ValueTask<ClusterWriteOutcome> WriteStrongLocalAsync(SqlStatementRequest request, CancellationToken cancellationToken)
        {
            var outcome = await session.ExecuteWriteAsync(request with { Mode = ConsistencyMode.Strong }, cancellationToken);
            return outcome switch
            {
                SqlWriteResult written => written,
                StrongRequired => new ClusterUnavailable("local strong write refused", "strong_required"),
                SqlError error => error,
                SchemaMismatch mismatch => mismatch,
                StorageBusy busy => busy,
                StorageTimeout timeout => new ClusterTimeout(timeout.Message, timeout.Code),
            };
        }

        public async ValueTask<ClusterBeginTransactionOutcome> BeginTransactionAsync(SqlTransactionOptions options, CancellationToken cancellationToken)
        {
            var outcome = await session.BeginTransactionAsync(options, cancellationToken);
            return outcome switch
            {
                ISqlTransaction transaction => new ClusterBeginTransactionOutcome(transaction),
                StorageBusy busy => busy,
                StorageTimeout timeout => new ClusterTimeout(timeout.Message, timeout.Code),
            };
        }

        public async ValueTask<ClusterCommitTransactionOutcome> CommitAsync(ISqlTransaction transaction, CancellationToken cancellationToken)
        {
            var outcome = await transaction.CommitLocalAsync(cancellationToken);
            return outcome switch
            {
                CommitMetadata commit => commit,
                StrongRequired => new ClusterUnavailable("strong commit required", "strong_required"),
                SchemaMismatch mismatch => mismatch,
                StorageBusy busy => busy,
                StorageTimeout timeout => new ClusterTimeout(timeout.Message, timeout.Code),
            };
        }

        public IAsyncEnumerable<ChangefeedEntry> SubscribeChangesAsync(CausalBookmark after, CancellationToken cancellationToken) =>
            session.SubscribeChangesAsync(after, cancellationToken);
    }

    private readonly record struct PgQuerySummary(int RowCount, string CommandTag, char ReadyStatus);

    private sealed class PgWireLoopbackClient : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly PgMessageBuffer _outbound = new();
        private readonly byte[] _header = new byte[5];
        private byte[] _payload = new byte[4096];

        private PgWireLoopbackClient(TcpClient tcp, NetworkStream stream)
        {
            _tcp = tcp;
            _stream = stream;
        }

        public static async Task<PgWireLoopbackClient> ConnectAsync(
            int port,
            string user,
            string database,
            string password,
            CancellationToken cancellationToken)
        {
            var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            var client = new PgWireLoopbackClient(tcp, tcp.GetStream());
            await client.AuthenticateAsync(user, database, password, cancellationToken);
            return client;
        }

        public async Task<PgQuerySummary> SimpleQueryAsync(string sql, CancellationToken cancellationToken)
        {
            _outbound.Clear();
            _outbound.Begin('Q');
            _outbound.WriteCString(sql);
            _outbound.End();
            await FlushOutboundAsync(cancellationToken);
            return await ReadUntilReadyAsync(expectParseComplete: false, expectBindComplete: false, cancellationToken);
        }

        public async Task<PgQuerySummary> ExtendedSelectByIdAsync(long id, CancellationToken cancellationToken)
        {
            _outbound.Clear();
            _outbound.Begin('P');
            _outbound.WriteCString(string.Empty);
            _outbound.WriteCString("SELECT id, name, score FROM items WHERE id = $1::int8");
            _outbound.WriteInt16(1);
            _outbound.WriteInt32(PgOid.Int8);
            _outbound.End();

            _outbound.Begin('B');
            _outbound.WriteCString(string.Empty);
            _outbound.WriteCString(string.Empty);
            _outbound.WriteInt16(1);
            _outbound.WriteInt16(1);
            _outbound.WriteInt16(1);
            _outbound.WriteInt32(8);
            _outbound.WriteInt64(id);
            _outbound.WriteInt16(1);
            _outbound.WriteInt16(1);
            _outbound.End();

            _outbound.Begin('E');
            _outbound.WriteCString(string.Empty);
            _outbound.WriteInt32(0);
            _outbound.End();

            _outbound.Begin('S');
            _outbound.End();

            await FlushOutboundAsync(cancellationToken);
            return await ReadUntilReadyAsync(expectParseComplete: true, expectBindComplete: true, cancellationToken);
        }

        public async Task<PgQuerySummary> ExtendedUpdateScoreAsync(long id, long score, CancellationToken cancellationToken)
        {
            _outbound.Clear();
            _outbound.Begin('P');
            _outbound.WriteCString(string.Empty);
            _outbound.WriteCString("UPDATE items SET score = $1::int8 WHERE id = $2::int8");
            _outbound.WriteInt16(2);
            _outbound.WriteInt32(PgOid.Int8);
            _outbound.WriteInt32(PgOid.Int8);
            _outbound.End();

            _outbound.Begin('B');
            _outbound.WriteCString(string.Empty);
            _outbound.WriteCString(string.Empty);
            _outbound.WriteInt16(1);
            _outbound.WriteInt16(1);
            _outbound.WriteInt16(2);
            _outbound.WriteInt32(8);
            _outbound.WriteInt64(score);
            _outbound.WriteInt32(8);
            _outbound.WriteInt64(id);
            _outbound.WriteInt16(0);
            _outbound.End();

            _outbound.Begin('E');
            _outbound.WriteCString(string.Empty);
            _outbound.WriteInt32(0);
            _outbound.End();

            _outbound.Begin('S');
            _outbound.End();

            await FlushOutboundAsync(cancellationToken);
            return await ReadUntilReadyAsync(expectParseComplete: true, expectBindComplete: true, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            _outbound.Clear();
            _outbound.Begin('X');
            _outbound.End();
            if (_tcp.Connected)
            {
                await _stream.WriteAsync(_outbound.WrittenSpan.ToArray(), CancellationToken.None);
                await _stream.FlushAsync(CancellationToken.None);
            }

            _outbound.Dispose();
            await _stream.DisposeAsync();
            _tcp.Dispose();
        }

        private async Task AuthenticateAsync(string user, string database, string password, CancellationToken cancellationToken)
        {
            _outbound.Clear();
            var start = _outbound.ReserveInt32();
            _outbound.WriteInt32(PgFrameReader.ProtocolVersion3);
            _outbound.WriteCString("user");
            _outbound.WriteCString(user);
            _outbound.WriteCString("database");
            _outbound.WriteCString(database);
            _outbound.WriteCString("application_name");
            _outbound.WriteCString("terrabase-bench");
            _outbound.WriteByte(0);
            _outbound.PatchInt32(start, _outbound.Length);
            await FlushOutboundAsync(cancellationToken);

            var (authType, authLength) = await ReadFrameAsync(cancellationToken);
            if (authType != 'R' || authLength != 4 || BinaryPrimitives.ReadInt32BigEndian(_payload.AsSpan(0, 4)) != 3)
            {
                throw new InvalidOperationException($"Expected CleartextPassword auth request, received '{authType}'");
            }

            _outbound.Clear();
            _outbound.Begin('p');
            _outbound.WriteCString(password);
            _outbound.End();
            await FlushOutboundAsync(cancellationToken);

            var authenticated = false;
            while (true)
            {
                var (type, length) = await ReadFrameAsync(cancellationToken);
                if (type == 'E')
                {
                    throw new InvalidOperationException($"Postgres startup error: {DecodeError(length)}");
                }

                if (type == 'R' && length == 4 && BinaryPrimitives.ReadInt32BigEndian(_payload.AsSpan(0, 4)) == 0)
                {
                    authenticated = true;
                }
                else if (type == 'Z')
                {
                    if (!authenticated || length != 1 || _payload[0] != (byte)'I')
                    {
                        throw new InvalidOperationException("Postgres session did not reach authenticated idle state");
                    }

                    return;
                }
            }
        }

        private async Task<PgQuerySummary> ReadUntilReadyAsync(bool expectParseComplete, bool expectBindComplete, CancellationToken cancellationToken)
        {
            var rows = 0;
            var tag = string.Empty;
            var gotParse = false;
            var gotBind = false;
            while (true)
            {
                var (type, length) = await ReadFrameAsync(cancellationToken);
                switch (type)
                {
                    case '1':
                        gotParse = true;
                        break;
                    case '2':
                        gotBind = true;
                        break;
                    case 'D':
                        rows++;
                        break;
                    case 'C':
                        tag = length > 0 && _payload[length - 1] == 0
                            ? Encoding.UTF8.GetString(_payload, 0, length - 1)
                            : Encoding.UTF8.GetString(_payload, 0, length);
                        break;
                    case 'E':
                        throw new InvalidOperationException($"Postgres wire error: {DecodeError(length)}");
                    case 'Z':
                        if (expectParseComplete && !gotParse)
                        {
                            throw new InvalidOperationException("Missing ParseComplete ('1') in extended protocol response");
                        }

                        if (expectBindComplete && !gotBind)
                        {
                            throw new InvalidOperationException("Missing BindComplete ('2') in extended protocol response");
                        }

                        var status = length == 1 ? (char)_payload[0] : '?';
                        if (status != 'I')
                        {
                            throw new InvalidOperationException($"Unexpected ReadyForQuery status '{status}'");
                        }

                        return new PgQuerySummary(rows, tag, status);
                }
            }
        }

        private async Task FlushOutboundAsync(CancellationToken cancellationToken)
        {
            var bytes = _outbound.WrittenSpan.ToArray();
            await _stream.WriteAsync(bytes, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }

        private async Task<(char Type, int PayloadLength)> ReadFrameAsync(CancellationToken cancellationToken)
        {
            await _stream.ReadExactlyAsync(_header, cancellationToken);
            var type = (char)_header[0];
            var length = BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(1, 4));
            if (length < 4)
            {
                throw new InvalidOperationException($"Invalid Postgres backend frame length {length.ToString(CultureInfo.InvariantCulture)}");
            }

            var bodyLength = length - 4;
            if (bodyLength > _payload.Length)
            {
                _payload = new byte[Math.Max(_payload.Length * 2, bodyLength)];
            }

            if (bodyLength > 0)
            {
                await _stream.ReadExactlyAsync(_payload.AsMemory(0, bodyLength), cancellationToken);
            }

            return (type, bodyLength);
        }

        private string DecodeError(int length)
        {
            var span = _payload.AsSpan(0, length);
            var index = 0;
            while (index < span.Length && span[index] != 0)
            {
                var field = (char)span[index++];
                var end = span[index..].IndexOf((byte)0);
                if (end < 0)
                {
                    break;
                }

                if (field == 'M')
                {
                    return Encoding.UTF8.GetString(span.Slice(index, end));
                }

                index += end + 1;
            }

            return "unknown error";
        }
    }
}
