using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.HomeApply;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Sql;
using TerraBase.Features.Sql.Routing;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replicas;

internal sealed record HomeSessionOpen(string Organization, string Project, string Database, string Region, bool CanWrite, ulong? PushThrough, long Epoch);

internal sealed record HomeSessionCall(
    string Op, string Sql, IReadOnlyList<SqlValue> Args, IReadOnlyDictionary<string, SqlValue> Named, IReadOnlyList<HomeSessionCall>? Batch = null,
    ulong? PushThrough = null);

internal sealed record HomeSessionResult(IReadOnlyList<SqlColumn> Columns, IReadOnlyList<IReadOnlyList<SqlValue>> Rows, long RowsAffected, long? LastInsertRowid);

internal sealed record HomeSessionAnswer(
    bool Autocommit, SqlError? Error = null, HomeSessionResult? Result = null, SqlDescription? Description = null, IReadOnlyList<HomeSessionResult>? Batch = null, int Statement = 0);

internal static class HomeSessionFrames
{
    internal const int MaxBytes = 24 * 1024 * 1024;

    public static async ValueTask<T?> ReadAsync<T>(PipeReader input, JsonTypeInfo<T> type, IMemoryBudget memory, TenantContext? tenant, CancellationToken cancellationToken)
        where T : class
    {
        var header = await input.ReadAtLeastAsync(4, cancellationToken).ConfigureAwait(false);
        if (header.Buffer.Length < 4)
        {
            input.AdvanceTo(header.Buffer.End);
            return header.Buffer.IsEmpty ? null : throw new InvalidDataException("a home session frame ended early");
        }

        var length = Length(header.Buffer);
        input.AdvanceTo(header.Buffer.GetPosition(4));
        if (length is < 2 or > MaxBytes)
        {
            throw new InvalidDataException("a home session frame holds 2 bytes to 24 MiB");
        }

        using var reservation = memory.TryReserve(length, tenant) ?? throw new QosMemoryException();
        var body = await input.ReadAtLeastAsync(length, cancellationToken).ConfigureAwait(false);
        if (body.Buffer.Length < length)
        {
            input.AdvanceTo(body.Buffer.End);
            throw new InvalidDataException("a home session frame ended early");
        }

        var frame = body.Buffer.Slice(0, length);
        try
        {
            return Deserialize(frame, type) ?? throw new InvalidDataException("a home session frame is null");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("a home session frame is not valid JSON", error);
        }
        finally
        {
            input.AdvanceTo(frame.End);
        }
    }

    public static async ValueTask WriteAsync<T>(PipeWriter output, T value, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, type);
        if (json.Length > MaxBytes)
        {
            throw new InvalidDataException("a home session frame holds at most 24 MiB");
        }

        var prefix = output.GetSpan(4);
        BinaryPrimitives.WriteInt32BigEndian(prefix, json.Length);
        output.Advance(4);
        output.Write(json);
        _ = await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int Length(ReadOnlySequence<byte> buffer)
    {
        Span<byte> prefix = stackalloc byte[4];
        buffer.Slice(0, 4).CopyTo(prefix);
        return BinaryPrimitives.ReadInt32BigEndian(prefix);
    }

    private static T? Deserialize<T>(ReadOnlySequence<byte> frame, JsonTypeInfo<T> type)
    {
        var reader = new Utf8JsonReader(frame);
        return JsonSerializer.Deserialize(ref reader, type);
    }
}

internal sealed class HomeSessions(
    TerraBaseOptions options, IReplicaPeers peers, InternalCredential credential, IMemoryBudget memory, HomePush.ILocalWrites local) : IDisposable
{
    internal const string Path = "/v1/internal/sql/session";

    private readonly HttpClient _http = CreateClient(credential);

    public async ValueTask<HomeSessionOutcome> OpenAsync(TenantContext database, string home, long epoch, bool canWrite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var unreachable = Unreachable(home);
        if ((await peers.ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(peer => peer.Region == home) is not { } node)
        {
            return unreachable;
        }

        var upstream = new Pipe();
        var stop = new CancellationTokenSource();
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(node.Internal, Path)) { Content = new Upstream(upstream.Reader) };
        try
        {
            await HomeSessionFrames.WriteAsync(upstream.Writer,
                new HomeSessionOpen(database.OrganizationId, database.ProjectId!, database.DatabaseId!, options.Region, canWrite, local.LastPushed(database), epoch),
                TerraBaseJsonContext.Default.HomeSessionOpen, cancellationToken).ConfigureAwait(false);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                response.Dispose();
                throw new HttpRequestException("the home refused the session", null, response.StatusCode);
            }

            var downstream = PipeReader.Create(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
            var session = new HomeSqlSession(request, response, upstream.Writer, downstream, stop, memory, database, unreachable, local);
            switch (await session.ReceiveAsync(cancellationToken).ConfigureAwait(false))
            {
                case HomeSessionAnswer { Error: { } refused }:
                    await session.DisposeAsync().ConfigureAwait(false);
                    return refused;
                case HomeSessionAnswer:
                    return session;
                default:
                    await session.DisposeAsync().ConfigureAwait(false);
                    return unreachable;
            }
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException
            || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            await stop.CancelAsync().ConfigureAwait(false);
            request.Dispose();
            stop.Dispose();
            return unreachable;
        }
    }

    public void Dispose() => _http.Dispose();

    internal static SqlError Unreachable(string home) =>
        new(SqlErrors.Unavailable, $"the database's home region {home} cannot be reached now; reads keep working here, retry writes");

    private static HttpClient CreateClient(InternalCredential credential)
    {
        var client = PeerHttp.Create(credential);
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }

    private sealed class Upstream(PipeReader input) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            while (true)
            {
                var read = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
                foreach (var segment in read.Buffer)
                {
                    await stream.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                input.AdvanceTo(read.Buffer.End);
                if (read.IsCompleted || read.IsCanceled)
                {
                    await input.CompleteAsync().ConfigureAwait(false);
                    return;
                }
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}

internal readonly union HomeSessionOutcome(HomeSqlSession, SqlError);

internal sealed class HomeSqlSession(
    HttpRequestMessage request, HttpResponseMessage response, PipeWriter upstream, PipeReader downstream, CancellationTokenSource stop,
    IMemoryBudget memory, TenantContext database, SqlError unreachable, HomePush.ILocalWrites local) : ISqlSession
{
    private static readonly IReadOnlyDictionary<string, SqlValue> NoNamed = new Dictionary<string, SqlValue>();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _autocommit = true;
    private bool _broken;
    private int _disposed;

    public bool IsAutocommit => _autocommit;

    public bool IsBroken => _broken;

    public void BeginRequest()
    {
    }

    public ValueTask<SqlOutcome> ExecuteAsync(SqlPlannedCommand planned, CancellationToken cancellationToken) =>
        ExecuteAsync(planned.Plan, planned.Command, admit: null, cancellationToken);

    public ValueTask<SqlOutcome> ExecuteAsync(SqlStatementPlan plan, SqlCommand command, CancellationToken cancellationToken) =>
        ExecuteAsync(plan, command, admit: null, cancellationToken);

    public async ValueTask<SqlOutcome> ExecuteAsync(SqlStatementPlan plan, SqlCommand command, Func<SqlResult, SqlError?>? admit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return await CallAsync(new HomeSessionCall("execute", command.Sql, command.Args, command.Named), cancellationToken).ConfigureAwait(false) switch
        {
            { Error: { } error } => error,
            { Result: { } result } => new SqlResult(result.Columns, result.Rows, result.RowsAffected, result.LastInsertRowid) switch
            {
                var answered when admit?.Invoke(answered) is { } refused => refused,
                var answered => answered,
            },
            _ => unreachable,
        };
    }

    public async ValueTask<SqlBatchOutcome> BatchAsync(IReadOnlyList<SqlCommand> commands, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var batch = commands.Select(command => new HomeSessionCall("execute", command.Sql, command.Args, command.Named)).ToList();
        return await CallAsync(new HomeSessionCall("batch", "", [], NoNamed, batch), cancellationToken).ConfigureAwait(false) switch
        {
            { Error: { } error } answer => new SqlBatchFailure(error, answer.Statement),
            { Batch: { } results } => new SqlExecuteBody([.. results.Select(result => new SqlStatementBody(
                [.. result.Columns.Select(column => new SqlColumnBody(column.Name, column.DeclType))], result.Rows, result.RowsAffected, result.LastInsertRowid))]),
            _ => new SqlBatchFailure(unreachable, 0),
        };
    }

    public ValueTask<SqlBatchOutcome?> TryBatchAsync(IReadOnlyList<SqlPlannedCommand> commands, CancellationToken cancellationToken) =>
        ValueTask.FromResult<SqlBatchOutcome?>(null);

    public void Prefer(WriteOverride preference) => _ = preference;

    public WriteRoute? LastRoute => null;

    public void KeepCommitted()
    {
    }

    public IReadOnlyList<IReadOnlyList<CapturedRowChange>> Committed => [];

    public ValueTask<SqlOutcome> ExecuteScriptAsync(string sql, CancellationToken cancellationToken) =>
        ValueTask.FromResult<SqlOutcome>(SqlErrors.Error("scripts are not run over a home session"));

    public ValueTask<SqlOutcome> ExecuteScriptAsync(string sql, int maxStatements, CancellationToken cancellationToken) =>
        ExecuteScriptAsync(sql, cancellationToken);

    public async ValueTask<SqlDescribeOutcome> DescribeAsync(SqlStatementPlan plan, SqlCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return await CallAsync(new HomeSessionCall("describe", command.Sql, [], NoNamed), cancellationToken).ConfigureAwait(false) switch
        {
            { Error: { } error } => error,
            { Description: { } description } => description,
            _ => unreachable,
        };
    }

    public ValueTask EndRequestAsync() => ValueTask.CompletedTask;

    internal async ValueTask<HomeSessionAnswer?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var answer = await HomeSessionFrames.ReadAsync(downstream, TerraBaseJsonContext.Default.HomeSessionAnswer, memory, database, cancellationToken).ConfigureAwait(false);
        if (answer is not null)
        {
            _autocommit = answer.Autocommit;
        }

        return answer;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await upstream.CompleteAsync().ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        await downstream.CompleteAsync().ConfigureAwait(false);
        response.Dispose();
        request.Dispose();
        stop.Dispose();
        _gate.Dispose();
    }

    private async ValueTask<HomeSessionAnswer?> CallAsync(HomeSessionCall call, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_broken)
            {
                return null;
            }

            try
            {
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bounded.CancelAfter(SqlLimits.ForwardedRequestTime);
                await HomeSessionFrames.WriteAsync(upstream, call with { PushThrough = local.LastPushed(database) }, TerraBaseJsonContext.Default.HomeSessionCall,
                    bounded.Token).ConfigureAwait(false);
                var answer = await ReceiveAsync(bounded.Token).ConfigureAwait(false) ?? Break();
                local.Forwarded(database);
                return answer;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or OperationCanceledException or ObjectDisposedException)
            {
                Break();
                if (error is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private HomeSessionAnswer? Break()
    {
        (_broken, _autocommit) = (true, true);
        return null;
    }
}
