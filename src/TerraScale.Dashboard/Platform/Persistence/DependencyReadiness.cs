using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace TerraScale.Dashboard.Platform.Persistence
{
    // Readiness names each dependency a signed-in request needs: ts-auth (its own unauthenticated /readyz) and the
    // TerraBase state. Public bodies carry the region and the build revision, which also travels in a header.
    public sealed class DependencyReadiness(IDbContextFactory<DashboardDbContext> storage, DashboardStateInitializer initializer, IHttpClientFactory http, IConfiguration configuration,
        TimeProvider time, ILogger<DependencyReadiness> logger)
    {
        private static readonly Uri AuthReady = new("https://auth.terrascale.tech/readyz");
        // The SDK's informational version carries the SourceRevisionId that the image build passes (+<sha>).
        public string Revision { get; } = typeof(DependencyReadiness).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[^1] ?? "unknown";
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);
        // Probes from ts-orch, ts-status and the internet share one ts-auth check for this long.
        private static readonly TimeSpan AuthReuse = TimeSpan.FromSeconds(5);
        private AuthCheck? auth;

        // ts-orch sets TS_REGION on every node; it is null outside ts-orch.
        public string? Region { get; } = configuration["TS_REGION"] is { Length: > 0 } region ? region : null;

        public async Task<IResult> CheckAsync(HttpContext context, CancellationToken cancellationToken)
        {
            context.Response.Headers["X-TerraScale-Revision"] = Revision;
            var tsAuth = AuthAsync();
            var terraBase = await TerraBaseAsync(cancellationToken);
            var authReady = await tsAuth.WaitAsync(cancellationToken);
            var ready = authReady && terraBase;
            return Results.Json(new
            {
                status = ready ? "ok" : "unavailable",
                region = Region,
                version = Revision,
                dependencies = new Dictionary<string, string> { ["ts-auth"] = Name(authReady), ["terrabase"] = Name(terraBase) }
            }, statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        }

        private static string Name(bool ready)
        {
            return ready ? "ok" : "unavailable";
        }

        private Task<bool> AuthAsync()
        {
            var now = time.GetUtcNow();
            if (Volatile.Read(ref auth) is { } current && now - current.At < AuthReuse)
            {
                return current.Ready;
            }

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
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                logger.ReadinessTsAuthAnswered((int)response.StatusCode);
            }
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
            {
                logger.ReadinessTsAuthUnreachable(error.GetType().Name);
            }
            return false;
        }

        private async Task<bool> TerraBaseAsync(CancellationToken cancellationToken)
        {
            // Startup keeps retrying the schema while TerraBase is unreachable; until it succeeds, the state is not ready.
            if (!initializer.Initialized)
            {
                return false;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Deadline);
            try
            {
                await using var db = await storage.CreateDbContextAsync(deadline.Token);
                return !(await db.Database.GetPendingMigrationsAsync(deadline.Token)).Any();
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                logger.ReadinessTerraBaseStateUnavailable(error.GetType().Name);
                return false;
            }
        }

        private sealed record AuthCheck(DateTimeOffset At, Task<bool> Ready);
    }
}
