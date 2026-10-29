namespace TerraScale.Dashboard.Platform.Persistence;

// Readiness names each dependency a signed-in request needs: ts-auth (its own unauthenticated /readyz) and the
// TerraBase state. Public bodies carry the region and never the build revision.
public sealed class DependencyReadiness(IDashboardState state, DashboardStateInitializer initializer, IHttpClientFactory http, IConfiguration configuration,
    TimeProvider time, ILogger<DependencyReadiness> logger)
{
    private static readonly Uri AuthReady = new("https://auth.terrascale.tech/readyz");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);
    // Probes from ts-orch, ts-status and the internet share one ts-auth check for this long.
    private static readonly TimeSpan AuthReuse = TimeSpan.FromSeconds(5);
    private AuthCheck? auth;

    // ts-orch sets TS_REGION on every node; it is null outside ts-orch.
    public string? Region { get; } = configuration["TS_REGION"] is { Length: > 0 } region ? region : null;

    public async Task<IResult> CheckAsync(CancellationToken cancellationToken)
    {
        var tsAuth = AuthAsync();
        var terraBase = await TerraBaseAsync(cancellationToken);
        var authReady = await tsAuth.WaitAsync(cancellationToken);
        var ready = authReady && terraBase;
        return Results.Json(new
        {
            status = ready ? "ok" : "unavailable",
            region = Region,
            dependencies = new Dictionary<string, string> { ["ts-auth"] = Name(authReady), ["terrabase"] = Name(terraBase) }
        }, statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }

    private static string Name(bool ready) => ready ? "ok" : "unavailable";

    private Task<bool> AuthAsync()
    {
        var now = time.GetUtcNow();
        if (Volatile.Read(ref auth) is { } current && now - current.At < AuthReuse) return current.Ready;
        var check = new AuthCheck(now, ProbeAuthAsync());
        Volatile.Write(ref auth, check);
        return check.Ready;
    }

    // The shared check owns its deadline, so one caller's cancellation never fails the others.
    private async Task<bool> ProbeAuthAsync()
    {
        try
        {
            using var deadline = new CancellationTokenSource(Deadline);
            using var response = await http.CreateClient("platform").GetAsync(AuthReady, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.IsSuccessStatusCode) return true;
            logger.LogWarning("Readiness: ts-auth answered {StatusCode}", (int)response.StatusCode);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            logger.LogWarning("Readiness: ts-auth unreachable ({ErrorType})", error.GetType().Name);
        }
        return false;
    }

    private async Task<bool> TerraBaseAsync(CancellationToken cancellationToken)
    {
        // Startup keeps retrying the schema while TerraBase is unreachable; until it succeeds, the state is not ready.
        if (!initializer.Initialized) return false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);
        try
        {
            await state.CheckReadyAsync(deadline.Token);
            return true;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Readiness: TerraBase state unavailable ({ErrorType})", error.GetType().Name);
            return false;
        }
    }

    private sealed record AuthCheck(DateTimeOffset At, Task<bool> Ready);
}
