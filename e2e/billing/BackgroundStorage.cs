using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

internal sealed class BackgroundStorage(BackgroundOwner owner, BackgroundCommands commands) : IDisposable
{
    private readonly HttpClient client = new(new RenewalTransport(commands) { InnerHandler = TerraBaseHttp.CreateHandler() });
    public TerraBaseSqlDatabase Database => new TerraBaseSqlClient(client, new() { ApiKey = owner.ApiKey, Region = owner.DatabaseRegion.ToLowerInvariant() }).Database(owner.ProjectId, owner.SecondDatabaseName);
    public void Dispose() => client.Dispose();

    private sealed class RenewalTransport(BackgroundCommands commands) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var drop = request.Content is not null && commands.DropRenewal(await request.Content.ReadAsStringAsync(cancellationToken));
            var response = await base.SendAsync(request, cancellationToken);
            if (!drop) { return response; }
            response.Dispose();
            throw new HttpRequestException("The journey discarded a real TerraBase lease-renewal reply.");
        }
    }

    public static void Configure(IServiceCollection services, BackgroundOwner owner, BackgroundCommands commands)
    {
        services.AddSingleton(_ => new BackgroundStorage(owner, commands));
        services.RemoveAll<TerraBaseSqlDatabase>();
        services.AddSingleton(provider => provider.GetRequiredService<BackgroundStorage>().Database);
        services.AddPooledDbContextFactory<DashboardDbContext>((_, options) => options.AddInterceptors(commands));
        services.AddSingleton<ILoggerProvider>(commands);
    }
}

internal sealed class BackgroundCommands : DbCommandInterceptor, ILoggerProvider
{
    private int calls;
    private int writes;
    private int refuseRenewal;
    private BackgroundClaimPause? pausedClaim;
    public string? LastWrite { get; private set; }
    public TaskCompletionSource RenewalObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public (int Calls, int Writes) Counts => (Volatile.Read(ref calls), Volatile.Read(ref writes));
    public void Reset() { Interlocked.Exchange(ref calls, 0); Interlocked.Exchange(ref writes, 0); LastWrite = null; }
    public void RefuseRenewal() => Interlocked.Exchange(ref refuseRenewal, 1);
    public BackgroundClaimPause PauseClaim(string id, bool afterWrite) => pausedClaim = new(id, afterWrite);

    private void Observe(DbCommand command)
    {
        Interlocked.Increment(ref calls);
        if (!command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) { Interlocked.Increment(ref writes); LastWrite = command.CommandText; }

    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Observe(command);
        await PauseAsync(command, false, cancellationToken);
        return result;
    }

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        await PauseAsync(command, true, cancellationToken);
        return result;
    }

    private async Task PauseAsync(DbCommand command, bool afterWrite, CancellationToken cancellationToken)
    {
        if (pausedClaim is not { } pause || pause.AfterWrite != afterWrite ||
            !command.CommandText.TrimStart().StartsWith("INSERT INTO background_jobs", StringComparison.Ordinal) ||
            !command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is string value && value == pause.Id)) { return; }
        pausedClaim = null;
        pause.Prefetched.TrySetResult();
        await pause.Resume.Task.WaitAsync(cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Observe(command);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Observe(command);
        return ValueTask.FromResult(result);
    }

    public bool DropRenewal(string request) => request.Contains("SET lease_until = strftime", StringComparison.Ordinal) && Interlocked.Exchange(ref refuseRenewal, 0) == 1;

    public ILogger CreateLogger(string categoryName) => new RenewalLogger(categoryName, RenewalObserved);
    public void Dispose() { }

    private sealed class RenewalLogger(string category, TaskCompletionSource observed) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (category.EndsWith("BackgroundJobs", StringComparison.Ordinal) && eventId.Id == 71) { observed.TrySetResult(); }
        }
    }
}

internal sealed class BackgroundClaimPause(string id, bool afterWrite)
{
    public string Id { get; } = id;
    public bool AfterWrite { get; } = afterWrite;
    public TaskCompletionSource Prefetched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
