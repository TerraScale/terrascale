using System.Buffers.Binary;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.HomeApply;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Replicas;
using TerraBase.Features.Sql.Routing;
using TerraBase.Features.Sql;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replicas;

internal sealed record HomeSessionOpen(string Organization, string Project, string Database, string Region, bool CanWrite, ulong? PushThrough, long Epoch);

internal sealed record HomeSessionCall(
    string Op, string Sql, IReadOnlyList<SqlValue> Args, IReadOnlyDictionary<string, SqlValue> Named, IReadOnlyList<HomeSessionCall>? Batch = null,
    ulong? PushThrough = null, TimeSpan? Remaining = null);

internal sealed record HomeSessionResult(IReadOnlyList<SqlColumn> Columns, IReadOnlyList<IReadOnlyList<SqlValue>> Rows, long RowsAffected, long? LastInsertRowid);

internal sealed record HomeSessionAnswer(
    bool Autocommit, SqlError? Error = null, HomeSessionResult? Result = null, SqlDescription? Description = null, IReadOnlyList<HomeSessionResult>? Batch = null, int Statement = 0);

internal static class HomeSessionFrames
{
    internal const int MaxBytes = 24 * 1024 * 1024;

    public static async ValueTask<T?> ReadAsync<T>(PipeReader input, JsonTypeInfo<T> type, IMemoryBudget memory, TenantContext? tenant, SessionMessageDeadline? deadline,
        CancellationToken cancellationToken)
        where T : class
    {
        var (header, buffered) = await FillAsync(input, 4, 0, deadline, cancellationToken).ConfigureAwait(false);
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
        var (body, _) = await FillAsync(input, length, buffered - 4, deadline, cancellationToken).ConfigureAwait(false);
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

    private static async ValueTask<(ReadResult Read, long Buffered)> FillAsync(PipeReader input, int minimum, long counted, SessionMessageDeadline? deadline,
        CancellationToken cancellationToken)
    {
        if (deadline is null)
        {
            var whole = await input.ReadAtLeastAsync(minimum, cancellationToken).ConfigureAwait(false);
            return (whole, whole.Buffer.Length);
        }

        while (true)
        {
            var read = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (read.Buffer.Length > counted)
            {
                deadline.Progress((int)(read.Buffer.Length - counted));
                counted = read.Buffer.Length;
            }

            if (read.Buffer.Length >= minimum || read.IsCompleted || read.IsCanceled)
            {
                return (read, counted);
            }

            input.AdvanceTo(read.Buffer.Start, read.Buffer.End);
        }
    }

    public static async ValueTask WriteAsync<T>(PipeWriter output, T value, JsonTypeInfo<T> type, Action? sending, CancellationToken cancellationToken)
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
        sending?.Invoke();
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
    TerraBaseOptions options, IReplicaPeers peers, InternalCredential credential, IMemoryBudget memory, HomePush.ILocalWrites local, TimeProvider time) : IDisposable
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
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var forwarding = new Upstream(upstream.Reader, stop.Token);
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(node.Internal, Path)) { Content = forwarding };
        try
        {
            await HomeSessionFrames.WriteAsync(upstream.Writer,
                new HomeSessionOpen(database.OrganizationId, database.ProjectId!, database.DatabaseId!, options.Region, canWrite, local.LastPushed(database), epoch),
                ReplicasJsonContext.Default.HomeSessionOpen, sending: null, cancellationToken).ConfigureAwait(false);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                response.Dispose();
                throw new HttpRequestException("the home refused the session", null, response.StatusCode);
            }

            var downstream = PipeReader.Create(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
            var session = new HomeSqlSession(request, response, forwarding, upstream.Writer, downstream, stop, memory, database, home, local, time);
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
            || error is OperationCanceledException)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await forwarding.DrainAsync().ConfigureAwait(false);
            request.Dispose();
            stop.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
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

    internal sealed class Upstream(PipeReader input, CancellationToken stop) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

        private Task _forwarding = Task.CompletedTask;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            _forwarding = ForwardAsync(stream, stop);

        internal async Task DrainAsync()
        {
            try
            {
                await _forwarding.ConfigureAwait(false);
            }
            catch (Exception error) when (stop.IsCancellationRequested && error is OperationCanceledException or IOException or HttpRequestException)
            {
                return;
            }
        }

        private async Task ForwardAsync(Stream stream, CancellationToken cancellationToken)
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
    HttpRequestMessage request, HttpResponseMessage response, HomeSessions.Upstream forwarding, PipeWriter upstream, PipeReader downstream, CancellationTokenSource stop,
    IMemoryBudget memory, TenantContext database, string home, HomePush.ILocalWrites local, TimeProvider time) : ISqlSession
{
    private static readonly IReadOnlyDictionary<string, SqlValue> NoNamed = new Dictionary<string, SqlValue>();
    private readonly SqlError _unreachable = HomeSessions.Unreachable(home);
    private readonly SqlError _unknown = SqlErrors.OutcomeUnknown(home);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _autocommit = true;
    private long _started;
    private bool _broken;
    private int _disposed;

    public bool IsAutocommit => _autocommit;

    public SqlTransactionClock TransactionClock { get; set; } = new(time);

    public TimeSpan? TransactionRemaining => _autocommit ? null : TransactionClock.Remaining;

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
        var commits = plan.Kind == SqlStatementKind.Commit || (_autocommit && plan.Kind == SqlStatementKind.Write);
        return await CallAsync(new HomeSessionCall("execute", command.Sql, command.Args, command.Named), commits, cancellationToken).ConfigureAwait(false) switch
        {
            { Error: { } error } => error,
            { Result: { } result } => new SqlResult(result.Columns, result.Rows, result.RowsAffected, result.LastInsertRowid) switch
            {
                var answered when admit?.Invoke(answered) is { } refused => refused,
                var answered => answered,
            },
            _ => _unreachable,
        };
    }

    public async ValueTask<SqlBatchOutcome> BatchAsync(IReadOnlyList<SqlPlannedCommand> commands, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var batch = commands.Select(planned => new HomeSessionCall("execute", planned.Command.Sql, planned.Command.Args, planned.Command.Named)).ToList();
        var commits = _autocommit && commands.Any(planned => planned.Plan.Kind == SqlStatementKind.Write);
        return await CallAsync(new HomeSessionCall("batch", "", [], NoNamed, batch), commits, cancellationToken).ConfigureAwait(false) switch
        {
            { Error: { } error } answer => new SqlBatchFailure(error, answer.Statement),
            { Batch: { } results } => new SqlExecuteBody([.. results.Select(result => new SqlStatementBody(
                [.. result.Columns.Select(column => new SqlColumnBody(column.Name, column.DeclType))], result.Rows, result.RowsAffected, result.LastInsertRowid))]),
            _ => new SqlBatchFailure(_unreachable, 0),
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
        return await CallAsync(new HomeSessionCall("describe", command.Sql, [], NoNamed), commits: false, cancellationToken).ConfigureAwait(false) switch
        {
            { Error: { } error } => error,
            { Description: { } description } => description,
            _ => _unreachable,
        };
    }

    public ValueTask EndRequestAsync() => ValueTask.CompletedTask;

    internal async ValueTask<HomeSessionAnswer?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var answer = await HomeSessionFrames.ReadAsync(downstream, ReplicasJsonContext.Default.HomeSessionAnswer, memory, database, deadline: null, cancellationToken).ConfigureAwait(false);
        if (answer is not null)
        {
            if (_autocommit && !answer.Autocommit)
            {
                TransactionClock.Limit(SqlLimits.MaxTransactionTime - time.GetElapsedTime(_started));
            }

            _autocommit = answer.Autocommit;
            if (_autocommit)
            {
                TransactionClock.End();
            }
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
        await forwarding.DrainAsync().ConfigureAwait(false);
        await downstream.CompleteAsync().ConfigureAwait(false);
        response.Dispose();
        request.Dispose();
        stop.Dispose();
        _gate.Dispose();
    }

    private async ValueTask<HomeSessionAnswer?> CallAsync(HomeSessionCall call, bool commits, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_broken)
            {
                return null;
            }

            var sent = false;
            try
            {
                _started = time.GetTimestamp();
                if (call.Op == "execute" && SqlScript.Classify(call.Sql) is SqlStatementPlan { Kind: SqlStatementKind.Begin })
                {
                    TransactionClock.Start();
                }

                await HomeSessionFrames.WriteAsync(upstream, call with { PushThrough = local.LastPushed(database), Remaining = TransactionClock.Remaining }, ReplicasJsonContext.Default.HomeSessionCall,
                    () => sent = true, cancellationToken).ConfigureAwait(false);
                if (await ReceiveAsync(cancellationToken).ConfigureAwait(false) is not { } answer)
                {
                    return Lost(commits && sent);
                }

                local.Forwarded(database);
                return answer;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or OperationCanceledException or ObjectDisposedException)
            {
                await stop.CancelAsync().ConfigureAwait(false);
                if (error is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    Break();
                    throw;
                }

                return Lost(commits && sent);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private HomeSessionAnswer? Lost(bool sentWrite)
    {
        Break();
        return sentWrite ? new HomeSessionAnswer(true, _unknown) : null;
    }

    private HomeSessionAnswer? Break()
    {
        (_broken, _autocommit) = (true, true);
        return null;
    }
}
