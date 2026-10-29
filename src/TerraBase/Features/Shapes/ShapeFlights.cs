using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;

namespace TerraBase.Features.Shapes;

internal interface IShapeFlightSealer
{
    ValueTask<ShapeFlightSealResult> SealAsync(IDatabaseStore store, CancellationToken cancellationToken);
}
internal readonly union ShapeFlightSealResult(Done, Unavailable);
internal sealed record ShapeFlightRequest(TenantContext Database, string Handle, string Offset, bool Live, Action? OnAdmitted = null);
internal delegate ValueTask<bool> ShapeHeadProbe(IDatabaseStore store, CancellationToken cancellationToken);
internal delegate ValueTask<T> ShapeTailReader<T>(IDatabaseStore store, CancellationToken cancellationToken);
internal sealed class ShapeFlight<T>(T value, IDisposable memory) : IDisposable
{
    public T Value { get; } = value;

    public void Dispose() => memory.Dispose();
}
internal readonly union ShapeFlightResult<T>(ShapeFlight<T>, QuotaExceeded, NotFound, Unavailable);
internal interface IShapeFlights<T>
{
    long NextCursor(long? input);
    ValueTask<ShapeFlightResult<T>> ReadAsync(
        ShapeFlightRequest request, ShapeHeadProbe probe, ShapeTailReader<T> read, CancellationToken cancellationToken);
}
internal sealed class ShapeFlights<T> : IShapeFlights<T>
{
    public static readonly TimeSpan LiveTimeout = TimeSpan.FromSeconds(20);

    internal const long LiveBucketSeconds = 20;
    internal const int MaxFlights = 32;
    internal const int MaxRequesters = 4096;
    internal const int MaxFlightsPerOrganization = 8;
    internal const int MaxRequestersPerOrganization = 1024;
    internal const long FlightBytes = 4 * 1024;
    internal const long RequesterBytes = 1024;

    private static readonly QuotaExceeded Busy = new("shape reads are at capacity; retry later", "shape_busy");
    private static readonly NotFound UnknownDatabase = new("the database does not exist", "unknown_database");

    private readonly Lock _gate = new();
    private readonly Dictionary<FlightKey, Flight> _flights = [];
    private readonly Dictionary<DatabaseKey, DatabaseState> _databases = [];
    private readonly Dictionary<string, OrganizationState> _organizations = [];
    private readonly IStorageEngine _engine;
    private readonly IShapeFlightSealer _sealer;
    private readonly IMemoryBudget _budget;
    private readonly TimeProvider _time;
    private readonly CancellationToken _stopping;
    private readonly IShapeMetrics? _metrics;
    private int _running;
    private int _requesters;

    public ShapeFlights(IStorageEngine engine, IShapeFlightSealer sealer, IMemoryBudget budget, TimeProvider time, IHostApplicationLifetime lifetime,
        IShapeMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        _metrics = metrics;
        _engine = engine;
        _sealer = sealer;
        _budget = budget;
        _time = time;
        _stopping = lifetime.ApplicationStopping;
    }
    public long NextCursor(long? input) => Math.Max(_time.GetUtcNow().ToUnixTimeSeconds() / LiveBucketSeconds, (input ?? -1) + 1);
    public async ValueTask<ShapeFlightResult<T>> ReadAsync(ShapeFlightRequest request, ShapeHeadProbe probe, ShapeTailReader<T> read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenant = request.Database with { Plan = QosContext.Current?.Plan ?? request.Database.Plan };
        var database = new DatabaseKey(tenant.OrganizationId, tenant.ProjectId, tenant.DatabaseId);
        var key = new FlightKey(database, tenant.Plan, request.Handle, request.Offset, request.Live);
        Flight? flight;
        var start = false;
        var hold = new QosContext.ResultMemory(_budget);
        lock (_gate)
        {
            var organization = _organizations.GetValueOrDefault(tenant.OrganizationId) ?? new OrganizationState();
            if (_requesters >= MaxRequesters || organization.Requesters >= MaxRequestersPerOrganization || !TryReserve(hold, RequesterBytes, tenant))
            {
                hold.Dispose();
                return Busy;
            }

            if (!_flights.TryGetValue(key, out flight))
            {
                var memory = new QosContext.ResultMemory(_budget);
                if (_running >= MaxFlights || organization.Flights >= MaxFlightsPerOrganization || !TryReserve(memory, FlightBytes, tenant))
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
                organization.Flights++;
                _running++;
                _metrics?.ChangeFlights(1);
                flight = new Flight(memory, state);
                _flights[key] = flight;
                start = true;
            }
            hold.Attach(flight.Memory);
            flight.Requesters++;
            organization.Requesters++;
            _requesters++;
            _organizations[tenant.OrganizationId] = organization;
        }

        if (start)
        {
            _ = RunAsync(request, probe, read, key, flight, tenant);
        }

        try
        {
            request.OnAdmitted?.Invoke();
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
    private void Settle(string organization)
    {
        if (_organizations[organization] is { Flights: 0, Requesters: 0 })
        {
            _organizations.Remove(organization);
        }
    }
    private void Leave(FlightKey key, Flight flight)
    {
        lock (_gate)
        {
            _requesters--;
            _organizations[key.Database.OrganizationId].Requesters--;
            Settle(key.Database.OrganizationId);
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
        Answer? answer = null;
        Exception? error = null;
        try
        {
            using var scope = QosContext.EnterFlight(tenant, flight.Memory);
            using var token = CancellationTokenSource.CreateLinkedTokenSource(_stopping, flight.Cancel.Token);
            answer = await AnswerAsync(request, probe, read, flight.Database, token.Token).ConfigureAwait(false);
        }
        catch (Exception caught)
        {
            error = caught;
        }

        lock (_gate)
        {
            if (_flights.TryGetValue(key, out var current) && current == flight)
            {
                _flights.Remove(key);
            }
            if (--flight.Database.Flights == 0)
            {
                _databases.Remove(key.Database);
            }

            _running--;
            _metrics?.ChangeFlights(-1);
            _organizations[key.Database.OrganizationId].Flights--;
            Settle(key.Database.OrganizationId);
        }
        flight.Memory.Dispose();
        if (error is null)
        {
            flight.Result.SetResult(answer!);
        }
        else
        {
            flight.Result.SetException(error);
            _ = flight.Result.Task.Exception;
        }
    }
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
        return await _engine.OpenDatabaseAsync(request.Database, cancellationToken).ConfigureAwait(false) is { } current
            ? new Answer(await read(current, cancellationToken).ConfigureAwait(false))
            : new Answer(default, Missing: UnknownDatabase);
    }
    private async Task<ShapeFlightSealResult> SealAsync(DatabaseState state, IDatabaseStore store, long through, CancellationToken cancellationToken)
    {
        while (true)
        {
            Seal? seal = null;
            Seal? drain = null;
            var start = false;
            lock (_gate)
            {
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

    private readonly record struct FlightKey(DatabaseKey Database, PlanTier Plan, string Handle, string Offset, bool Live);
    private sealed record Answer(T? Result, NotFound? Missing = null, Unavailable? Stuck = null);

    private sealed class Flight(QosContext.ResultMemory memory, DatabaseState database)
    {
        public QosContext.ResultMemory Memory { get; } = memory;

        public DatabaseState Database { get; } = database;

        public TaskCompletionSource<Answer> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Cancel { get; } = new();

        public int Requesters { get; set; }
    }

    private sealed class OrganizationState
    {
        public int Flights { get; set; }

        public int Requesters { get; set; }
    }

    private sealed class DatabaseState
    {
        public int Flights { get; set; }

        public Ulid Incarnation { get; set; }
        public long Generation { get; set; }
        public long Sealed { get; set; } = -1;

        public Seal? Running { get; set; }
    }

    private sealed class Seal(long generation, long covers)
    {
        public long Generation { get; } = generation;
        public long Covers { get; } = covers;

        public int Waiters { get; set; }
        public bool Abandoned { get; set; }

        public TaskCompletionSource<ShapeFlightSealResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Cancel { get; } = new();
    }
}
