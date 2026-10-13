using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;

namespace TerraBase.Features.Shapes;

/// <summary>The seal a live flight asks for on wake (free-reads.md 3.6); the endpoint adapts the chunk sealer to it.</summary>
internal interface IShapeFlightSealer
{
    /// <summary>
    /// Seals every shape of <paramref name="store"/>'s database up to the head committed when the call starts.
    /// <see cref="Unavailable"/> (<c>shape_too_large</c>) when the sealed head cannot advance.
    /// </summary>
    ValueTask<ShapeFlightSealResult> SealAsync(IDatabaseStore store, CancellationToken cancellationToken);
}

/// <summary>A seal up to the head, or why the sealed head cannot advance.</summary>
internal readonly union ShapeFlightSealResult(Done, Unavailable);

/// <summary>One shape request: the spec's collapsing key (handle, offset, live, cursor) in its database.</summary>
/// <param name="Database">The database, with the caller's plan.</param>
/// <param name="Handle">The shape handle.</param>
/// <param name="Offset">The offset exactly as requested.</param>
/// <param name="Live">Whether the request may wait at the sealed head.</param>
/// <param name="Cursor">The input live bucket; null without one. Ignored without <paramref name="Live"/>.</param>
internal sealed record ShapeFlightRequest(TenantContext Database, string Handle, string Offset, bool Live, long? Cursor);

/// <summary>Whether the request's offset is the sealed head, from sealed state only and without reading a payload.</summary>
internal delegate ValueTask<bool> ShapeHeadProbe(IDatabaseStore store, CancellationToken cancellationToken);

/// <summary>
/// Answers the request from sealed state only, under the flight's QoS context: reserve result bytes with
/// <see cref="QosContext.ReserveResult(long)"/> before allocating them.
/// </summary>
internal delegate ValueTask<T> ShapeTailReader<T>(IDatabaseStore store, CancellationToken cancellationToken);

/// <summary>A flight's answer and this requester's hold on the flight's reserved memory; dispose after the response is written.</summary>
internal sealed class ShapeFlight<T>(T value, IDisposable memory) : IDisposable
{
    /// <summary>The answer every requester of the flight shares.</summary>
    public T Value { get; } = value;

    public void Dispose() => memory.Dispose();
}

/// <summary>A flight's answer, or capacity (429 <c>shape_busy</c>), an unknown database (404) or a stuck seal (503).</summary>
internal readonly union ShapeFlightResult<T>(ShapeFlight<T>, QuotaExceeded, NotFound, Unavailable);

/// <summary>
/// Live tail and request collapsing (free-reads.md 3.6, shape-reads.md 4): one flight per (handle, offset, live,
/// cursor), at most <see cref="MaxFlights"/> running and <see cref="MaxRequesters"/> joined, without a queue.
/// </summary>
/// <remarks>
/// A flight runs on its own token: a requester leaving cancels only its own wait, the last one cancels the flight,
/// and application stop cancels every flight. An abandoned flight keeps its running slot until its work ends.
/// A live flight probes once; at the sealed head it waits up to <see cref="LiveTimeout"/> for a commit, and on wake
/// seals the database up to the head (one seal shared by every flight of that database). It reads its answer once.
/// A committed tail the request did not wake on is the background sealer's (FR-12). Register one closed type.
/// </remarks>
internal sealed class ShapeFlights<T>
{
    /// <summary>Longest live wait.</summary>
    public static readonly TimeSpan LiveTimeout = TimeSpan.FromSeconds(20);

    internal const long LiveBucketSeconds = 20;
    internal const int MaxFlights = 32;
    internal const int MaxRequesters = 256;

    // Admission for a flight's bookkeeping and for each requester's hold; result bytes are reserved by the reader.
    internal const long FlightBytes = 4 * 1024;
    internal const long RequesterBytes = 1024;

    private static readonly QuotaExceeded Busy = new("shape reads are at capacity; retry later", "shape_busy");
    private static readonly NotFound UnknownDatabase = new("the database does not exist", "unknown_database");

    private readonly Lock _gate = new();
    private readonly Dictionary<FlightKey, Flight> _flights = [];
    // One entry per database with a running flight, so at most MaxFlights.
    private readonly Dictionary<DatabaseKey, DatabaseState> _databases = [];
    private readonly IStorageEngine _engine;
    private readonly IShapeFlightSealer _sealer;
    private readonly IMemoryBudget _budget;
    private readonly TimeProvider _time;
    private readonly CancellationToken _stopping;
    private int _running;
    private int _requesters;

    public ShapeFlights(IStorageEngine engine, IShapeFlightSealer sealer, IMemoryBudget budget, TimeProvider time, IHostApplicationLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        _engine = engine;
        _sealer = sealer;
        _budget = budget;
        _time = time;
        _stopping = lifetime.ApplicationStopping;
    }

    /// <summary>The <c>TB-Shape-Cursor</c> of a live response: <c>max(floor(unix_s / 20), input + 1)</c>. The endpoint refuses <see cref="long.MaxValue"/>.</summary>
    public long NextCursor(long? input) => Math.Max(_time.GetUtcNow().ToUnixTimeSeconds() / LiveBucketSeconds, (input ?? -1) + 1);

    /// <summary>Joins or starts the flight for <paramref name="request"/>; <paramref name="probe"/> and <paramref name="read"/> run only when this call starts it.</summary>
    public async ValueTask<ShapeFlightResult<T>> ReadAsync(ShapeFlightRequest request, ShapeHeadProbe probe, ShapeTailReader<T> read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Storage identity from the request; the ambient context may be scoped above the database. A plan change
        // never joins a flight admitted under the old plan.
        var tenant = request.Database with { Plan = QosContext.Current?.Plan ?? request.Database.Plan };
        var database = new DatabaseKey(tenant.OrganizationId, tenant.ProjectId, tenant.DatabaseId);
        var key = new FlightKey(database, tenant.Plan, request.Handle, request.Offset, request.Live, request.Live ? request.Cursor : null);
        Flight? flight;
        var start = false;
        var hold = new QosContext.ResultMemory(_budget);
        lock (_gate)
        {
            if (_requesters >= MaxRequesters || !TryReserve(hold, RequesterBytes, tenant))
            {
                hold.Dispose();
                return Busy;
            }

            if (!_flights.TryGetValue(key, out flight))
            {
                var memory = new QosContext.ResultMemory(_budget);
                if (_running >= MaxFlights || !TryReserve(memory, FlightBytes, tenant))
                {
                    memory.Dispose();
                    hold.Dispose();
                    return Busy;
                }

                if (!_databases.TryGetValue(database, out var state))
                {
                    state = new DatabaseState();
                    _databases[database] = state;
                }

                state.Flights++;
                _running++;
                flight = new Flight(memory, state);
                _flights[key] = flight;
                start = true;
            }

            // In the table, the flight still owns its memory, so this attach cannot see it released.
            hold.Attach(flight.Memory);
            flight.Requesters++;
            _requesters++;
        }

        if (start)
        {
            _ = RunAsync(request, probe, read, key, flight, tenant);
        }

        try
        {
            var answer = await flight.Result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (answer.Missing is { } missing)
            {
                hold.Dispose();
                return missing;
            }

            if (answer.Stuck is { } stuck)
            {
                hold.Dispose();
                return stuck;
            }

            return new ShapeFlight<T>(answer.Result!, hold);
        }
        catch when (flight.Result.Task.IsFaulted)
        {
            // Error responses need the failed worker's partial reservations released first.
            hold.Dispose();
            await flight.Stopped.Task.ConfigureAwait(false);
            throw;
        }
        catch
        {
            hold.Dispose();
            throw;
        }
        finally
        {
            Leave(key, flight);
        }
    }

    // Memory refusal is the same bounded admission as capacity.
    private static bool TryReserve(QosContext.ResultMemory memory, long bytes, TenantContext tenant)
    {
        try
        {
            memory.Reserve(bytes, tenant);
            return true;
        }
        catch (QosMemoryException)
        {
            return false;
        }
    }

    // The last requester to leave an unfinished flight cancels it; a finished flight is already gone.
    private void Leave(FlightKey key, Flight flight)
    {
        lock (_gate)
        {
            _requesters--;
            if (--flight.Requesters > 0 || !_flights.TryGetValue(key, out var current) || current != flight)
            {
                return;
            }

            _flights.Remove(key);
        }

        flight.Cancel.Cancel();
    }

    private async Task RunAsync(ShapeFlightRequest request, ShapeHeadProbe probe, ShapeTailReader<T> read, FlightKey key, Flight flight, TenantContext tenant)
    {
        using var scope = QosContext.EnterFlight(tenant, flight.Memory);
        try
        {
            using var token = CancellationTokenSource.CreateLinkedTokenSource(_stopping, flight.Cancel.Token);
            flight.Result.SetResult(await AnswerAsync(request, probe, read, flight.Database, token.Token).ConfigureAwait(false));
        }
        catch (Exception error)
        {
            flight.Result.SetException(error);
            // Every requester may have left; the exception is theirs, not the process's.
            _ = flight.Result.Task.Exception;
        }
        finally
        {
            lock (_gate)
            {
                if (_flights.TryGetValue(key, out var current) && current == flight)
                {
                    _flights.Remove(key);
                }

                // A seal this flight started has ended (SealAsync), so no seal still uses the state.
                if (--flight.Database.Flights == 0)
                {
                    _databases.Remove(key.Database);
                }

                _running--;
            }

            flight.Memory.Dispose();
            flight.Stopped.SetResult();
        }
    }

    // Live: the commit count is taken before the probe, so a commit racing it wakes the wait. Each live flight probes
    // once, waits at most once, seals only on wake and reads once.
    private async Task<Answer> AnswerAsync(ShapeFlightRequest request, ShapeHeadProbe probe, ShapeTailReader<T> read, DatabaseState state, CancellationToken cancellationToken)
    {
        if (await _engine.OpenDatabaseAsync(request.Database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return new Answer(default, Missing: UnknownDatabase);
        }

        if (!request.Live)
        {
            return new Answer(await read(store, cancellationToken).ConfigureAwait(false));
        }

        using var timeout = new CancellationTokenSource(LiveTimeout, _time);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var sequence = store.CommitSequence;
        if (!await probe(store, cancellationToken).ConfigureAwait(false))
        {
            return new Answer(await read(store, cancellationToken).ConfigureAwait(false));
        }

        var woken = true;
        try
        {
            await store.WaitForCommitAsync(sequence, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            woken = false;
        }

        if (woken && await SealAsync(state, store, store.CommitSequence, cancellationToken).ConfigureAwait(false) is Unavailable stuck)
        {
            return new Answer(default, Stuck: stuck);
        }

        // Reopen: a wait on a deleted or replaced database is never signalled, and this read revalidates the
        // database and handle. After a timeout it is the fresh, unchanged answer at the head.
        return await _engine.OpenDatabaseAsync(request.Database, cancellationToken).ConfigureAwait(false) is { } current
            ? new Answer(await read(current, cancellationToken).ConfigureAwait(false))
            : new Answer(default, Missing: UnknownDatabase);
    }

    // Seals the database up to at least commit sequence `through`, once for every flight of the database: a flight
    // joins the running seal when it covers `through`, otherwise drains it and joins or starts the next one. The
    // watermark keeps a late flight from resealing what an earlier seal covered.
    private async Task<ShapeFlightSealResult> SealAsync(DatabaseState state, IDatabaseStore store, long through, CancellationToken cancellationToken)
    {
        while (true)
        {
            Seal? seal = null;
            Seal? drain = null;
            var start = false;
            lock (_gate)
            {
                // A new incarnation, or a reopened store whose count restarted, starts a new watermark generation.
                if (state.Incarnation != store.Incarnation || store.CommitSequence < state.Sealed)
                {
                    state.Incarnation = store.Incarnation;
                    state.Sealed = -1;
                    state.Generation++;
                }

                if (state.Sealed >= through)
                {
                    return new Done();
                }

                if (state.Running is { } running)
                {
                    if (running.Abandoned || running.Generation != state.Generation || running.Covers < through)
                    {
                        drain = running;
                    }
                    else
                    {
                        seal = running;
                    }
                }
                else
                {
                    seal = new Seal(state.Generation, store.CommitSequence);
                    state.Running = seal;
                    start = true;
                }

                if (seal is not null)
                {
                    seal.Waiters++;
                }
            }

            if (drain is not null)
            {
                // An abandoned, stale or earlier seal: wait for it to end, then retry. Ended completes after Running clears.
                await drain.Ended.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            var joined = seal!;
            if (start)
            {
                _ = RunSealAsync(state, store, joined);
            }

            var waiting = true;
            try
            {
                return await joined.Result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (start && cancellationToken.IsCancellationRequested)
            {
                waiting = false;
                Unwait(joined);
                // The starter keeps its flight slot, and with it this database's state, until the seal ends.
                await joined.Ended.Task.ConfigureAwait(false);
                throw;
            }
            finally
            {
                if (waiting)
                {
                    Unwait(joined);
                }
            }
        }
    }

    // The last flight waiting on a seal abandons and cancels it; later flights drain it instead of joining.
    private void Unwait(Seal seal)
    {
        lock (_gate)
        {
            if (--seal.Waiters > 0 || seal.Result.Task.IsCompleted)
            {
                return;
            }

            seal.Abandoned = true;
        }

        seal.Cancel.Cancel();
    }

    private async Task RunSealAsync(DatabaseState state, IDatabaseStore store, Seal seal)
    {
        await Task.Yield();
        ShapeFlightSealResult? result = null;
        Exception? error = null;
        try
        {
            using var token = CancellationTokenSource.CreateLinkedTokenSource(_stopping, seal.Cancel.Token);
            // The seal's memory is charged to its database, never to a flight that may leave first.
            using var scope = QosContext.Enter(store.Tenant, _budget);
            result = await _sealer.SealAsync(store, token.Token).ConfigureAwait(false);
        }
        catch (Exception caught)
        {
            error = caught;
        }

        lock (_gate)
        {
            state.Running = null;
            // A seal of an older incarnation or store never advances the current watermark.
            if (result is Done && state.Generation == seal.Generation)
            {
                state.Sealed = Math.Max(state.Sealed, seal.Covers);
            }
        }

        if (error is null)
        {
            seal.Result.SetResult(result!.Value);
        }
        else
        {
            seal.Result.SetException(error);
            _ = seal.Result.Task.Exception;
        }

        seal.Ended.SetResult();
    }

    private readonly record struct DatabaseKey(string OrganizationId, string? ProjectId, string? DatabaseId);

    private readonly record struct FlightKey(DatabaseKey Database, PlanTier Plan, string Handle, string Offset, bool Live, long? Cursor);

    // The reader's answer, or the database is missing, or its seal is stuck.
    private sealed record Answer(T? Result, NotFound? Missing = null, Unavailable? Stuck = null);

    private sealed class Flight(QosContext.ResultMemory memory, DatabaseState database)
    {
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public QosContext.ResultMemory Memory { get; } = memory;

        public DatabaseState Database { get; } = database;

        public TaskCompletionSource<Answer> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Never disposed: it has no timer, and a late Cancel must not throw.
        public CancellationTokenSource Cancel { get; } = new();

        public int Requesters { get; set; }
    }

    private sealed class DatabaseState
    {
        public int Flights { get; set; }

        public Ulid Incarnation { get; set; }

        // Advances with each watermark reset, so a seal of an older incarnation or store never counts.
        public long Generation { get; set; }

        // The commit sequence the last successful seal of this generation covers.
        public long Sealed { get; set; } = -1;

        public Seal? Running { get; set; }
    }

    private sealed class Seal(long generation, long covers)
    {
        public long Generation { get; } = generation;

        // The commit sequence read before the sealer started: every commit it counts is sealed on success.
        public long Covers { get; } = covers;

        public int Waiters { get; set; }

        // Its last waiter left and cancelled it; new flights drain it instead of joining.
        public bool Abandoned { get; set; }

        public TaskCompletionSource<ShapeFlightSealResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Never disposed: it has no timer, and a late Cancel must not throw.
        public CancellationTokenSource Cancel { get; } = new();
    }
}
