using System.Reflection;
using TerraScale.Dashboard.Platform.Clients;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Platform.Persistence
{
    public sealed class DependencyReadiness(DashboardDbUpRunner initializer, ServiceUrls urls, IHttpClientFactory http, IConfiguration configuration,
        TimeProvider time, ILogger<DependencyReadiness> logger)
    {
        private static readonly Uri AuthReady = new(TsAuthOrigin.Value, "readyz");
        private readonly Uri platformReady = new(urls.Platform, "readyz");
        public string Revision { get; } = typeof(DependencyReadiness).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[^1] ?? "unknown";
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ProbeReuse = TimeSpan.FromSeconds(5);
        private CachedCheck? auth;
        private CachedCheck? platform;

        public string? Region { get; } = configuration["TS_REGION"] is { Length: > 0 } region ? region : null;

        public async Task<IResult> CheckAsync(HttpContext context, CancellationToken cancellationToken)
        {
            context.Response.Headers["X-TerraScale-Revision"] = Revision;
            var tsAuth = AuthAsync();
            var tsPlatform = PlatformAsync();
            var terraBase = await TerraBaseAsync(cancellationToken);
            var authReady = await tsAuth.WaitAsync(cancellationToken);
            var platformReady = await tsPlatform.WaitAsync(cancellationToken);
            var ready = authReady && platformReady && terraBase;
            return TypedResults.Json(new
            {
                status = ready ? "ok" : "unavailable",
                region = Region,
                version = Revision,
                dependencies = new Dictionary<string, string>
                {
                    ["ts-auth"] = Name(authReady),
                    ["ts-platform"] = Name(platformReady),
                    ["terrabase"] = Name(terraBase),
                }
            }, statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        }

        private static string Name(bool ready)
        {
            return ready ? "ok" : "unavailable";
        }

        private Task<bool> AuthAsync()
        {
            var now = time.GetUtcNow();
            if (Volatile.Read(ref auth) is { } current && now - current.At < ProbeReuse)
            {
                return current.Ready;
            }

            var check = new CachedCheck(now, ProbeAuthAsync());
            Volatile.Write(ref auth, check);
            return check.Ready;
        }

        private Task<bool> PlatformAsync()
        {
            var now = time.GetUtcNow();
            if (Volatile.Read(ref platform) is { } current && now - current.At < ProbeReuse)
            {
                return current.Ready;
            }

            var check = new CachedCheck(now, ProbePlatformAsync());
            Volatile.Write(ref platform, check);
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
                logger.ReadinessTsAuthUnreachable(error.GetType().Name);
            }
            return false;
        }

        private async Task<bool> ProbePlatformAsync()
        {
            try
            {
                using var deadline = new CancellationTokenSource(Deadline);
                using var response = await http.CreateClient(PlatformHttpClient.HttpClientName).GetAsync(platformReady, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                logger.ReadinessTsPlatformAnswered((int)response.StatusCode);
            }
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
            {
                logger.ReadinessTsPlatformUnreachable(error.GetType().Name);
            }
            return false;
        }

        private async Task<bool> TerraBaseAsync(CancellationToken cancellationToken)
        {
            if (!initializer.Initialized)
            {
                return false;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Deadline);
            try
            {
                return (await initializer.PendingAsync(deadline.Token)).Count == 0;
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                logger.ReadinessTerraBaseStateUnavailable(error.GetType().Name);
                return false;
            }
        }

        private sealed record CachedCheck(DateTimeOffset At, Task<bool> Ready);
    }
}
