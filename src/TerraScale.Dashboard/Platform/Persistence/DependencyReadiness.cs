using System.Reflection;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Platform.Persistence
{
    public sealed class DependencyReadiness(DashboardDbUpRunner initializer, IHttpClientFactory http, IConfiguration configuration, TimeProvider time,
        ILogger<DependencyReadiness> logger)
    {
        private static readonly Uri AuthReady = new(TsAuthOrigin.Value, "readyz");
        public string Revision { get; } = typeof(DependencyReadiness).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[^1] ?? "unknown";
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ProbeReuse = TimeSpan.FromSeconds(5);
        private CachedCheck? auth;
        private CachedCheck? state;

        public string? Region { get; } = configuration["TS_REGION"] is { Length: > 0 } region ? region : null;

        public async Task<IResult> CheckAsync(HttpContext context, CancellationToken cancellationToken)
        {
            context.Response.Headers["X-TerraScale-Revision"] = Revision;
            var tsAuth = Reuse(ref auth, ProbeAuthAsync);
            var terraBase = initializer.Initialized ? Reuse(ref state, ProbeStateAsync) : Task.FromResult(false);
            var authReady = await tsAuth.WaitAsync(cancellationToken);
            var terraBaseReady = await terraBase.WaitAsync(cancellationToken);
            var ready = authReady && terraBaseReady;
            return TypedResults.Json(new
            {
                status = ready ? "ok" : "unavailable",
                region = Region,
                version = Revision,
                dependencies = new Dictionary<string, string>
                {
                    ["ts-auth"] = Name(authReady),
                    ["terrabase"] = Name(terraBaseReady),
                }
            }, statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        }

        private static string Name(bool ready)
        {
            return ready ? "ok" : "unavailable";
        }

        private Task<bool> Reuse(ref CachedCheck? slot, Func<Task<bool>> probe)
        {
            var now = time.GetUtcNow();
            if (Volatile.Read(ref slot) is { } current && now - current.At < ProbeReuse)
            {
                return current.Ready;
            }

            var check = new CachedCheck(now, probe());
            Volatile.Write(ref slot, check);
            return check.Ready;
        }

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
                logger.ReadinessTsAuthUnreachable(StorageFailure.Describe(error));
            }
            return false;
        }

        private async Task<bool> ProbeStateAsync()
        {
            using var deadline = new CancellationTokenSource(Deadline);
            try
            {
                return (await initializer.PendingAsync(deadline.Token)).Count == 0;
            }
            catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException or OperationCanceledException or InvalidDataException)
            {
                logger.ReadinessTerraBaseStateUnavailable(StorageFailure.Describe(error));
                return false;
            }
        }

        private sealed record CachedCheck(DateTimeOffset At, Task<bool> Ready);
    }
}
