using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

internal sealed class BackgroundStorage(BackgroundOwner owner, BackgroundCommands commands) : IDisposable
{
    private readonly HttpClient client = new(new SqlTransport(commands) { InnerHandler = TerraBaseHttp.CreateHandler() });
    public TerraBaseSqlDatabase Database => new TerraBaseSqlClient(client, new() { ApiKey = owner.ApiKey, Region = owner.DatabaseRegion.ToLowerInvariant() }).Database(owner.ProjectId, owner.SecondDatabaseName);
    public void Dispose() => client.Dispose();

    private sealed class SqlTransport(BackgroundCommands commands) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not { } target) { return await base.SendAsync(request, cancellationToken); }
            if (target.AbsolutePath.EndsWith("/sql/ws", StringComparison.Ordinal))
            {
                commands.ObserveTransaction();
                return await base.SendAsync(request, cancellationToken);
            }
            if (!target.AbsolutePath.EndsWith("/sql/execute", StringComparison.Ordinal) || request.Content is null) { return await base.SendAsync(request, cancellationToken); }
            var statements = BackgroundCommands.Statements(await request.Content.ReadAsStringAsync(cancellationToken));
            commands.Observe(statements);
            await commands.PauseAsync(statements, false, cancellationToken);
            var response = await base.SendAsync(request, cancellationToken);
            await commands.PauseAsync(statements, true, cancellationToken);
            if (!commands.DropRenewal(statements)) { return response; }
            response.Dispose();
            throw new HttpRequestException("The journey discarded a real TerraBase lease-renewal reply.");
        }
    }

    public static void Configure(IServiceCollection services, BackgroundOwner owner, BackgroundCommands commands)
    {
        services.AddSingleton(_ => new BackgroundStorage(owner, commands));
        services.RemoveAll<DashboardDb>();
        services.AddSingleton(provider => new DashboardDb(provider.GetRequiredService<BackgroundStorage>().Database));
        services.AddSingleton<ILoggerProvider>(commands);
    }
}

internal sealed record BackgroundStatement(string Sql, IReadOnlyList<string> Values);

internal sealed class BackgroundCommands : ILoggerProvider
{
    private const string Transaction = "transaction over a SQL session";
    private int calls;
    private int writes;
    private int refuseRenewal;
    private BackgroundClaimPause? pausedClaim;
    public string? LastWrite { get; private set; }
    public TaskCompletionSource RenewalObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public (int Calls, int Writes) Counts => (Volatile.Read(ref calls), Volatile.Read(ref writes));
    public void Reset() { Interlocked.Exchange(ref calls, 0); Interlocked.Exchange(ref writes, 0); LastWrite = null; }
    public void RefuseRenewal() => Interlocked.Exchange(ref refuseRenewal, 1);
    public BackgroundClaimPause PauseClaim(string id, bool afterWrite)
    {
        var pause = new BackgroundClaimPause(id, afterWrite);
        Volatile.Write(ref pausedClaim, pause);
        return pause;
    }

    public static IReadOnlyList<BackgroundStatement> Statements(string body)
    {
        using var json = JsonDocument.Parse(body);
        return [.. json.RootElement.GetProperty("statements").EnumerateArray().Select(statement => new BackgroundStatement(
            statement.GetProperty("sql").GetString() ?? throw new InvalidOperationException("A SQL statement has no text."),
            [.. Values(statement, "args"), .. Values(statement, "named")]))];
    }

    private static string[] Values(JsonElement statement, string name) =>
        !statement.TryGetProperty(name, out var values) ? []
        : (values.ValueKind == JsonValueKind.Array ? values.EnumerateArray() : values.EnumerateObject().Select(property => property.Value))
            .Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()).OfType<string>().ToArray();

    public void Observe(IReadOnlyList<BackgroundStatement> statements)
    {
        Interlocked.Increment(ref calls);
        if (statements.FirstOrDefault(statement => !statement.Sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) is not { } write) { return; }
        Interlocked.Increment(ref writes);
        LastWrite = write.Sql;
    }

    public void ObserveTransaction()
    {
        Interlocked.Increment(ref calls);
        Interlocked.Increment(ref writes);
        LastWrite = Transaction;
    }

    public async Task PauseAsync(IReadOnlyList<BackgroundStatement> statements, bool afterWrite, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref pausedClaim) is not { } pause || pause.AfterWrite != afterWrite ||
            !statements.Any(statement => statement.Sql.TrimStart().StartsWith("INSERT INTO background_jobs", StringComparison.Ordinal) && statement.Values.Contains(pause.Id, StringComparer.Ordinal)) ||
            Interlocked.CompareExchange(ref pausedClaim, null, pause) != pause) { return; }
        pause.Prefetched.TrySetResult();
        await pause.Resume.Task.WaitAsync(cancellationToken);
    }

    public bool DropRenewal(IReadOnlyList<BackgroundStatement> statements) =>
        statements.Any(statement => statement.Sql.Contains("SET lease_until = strftime", StringComparison.Ordinal)) && Interlocked.Exchange(ref refuseRenewal, 0) == 1;

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
