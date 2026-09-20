namespace TerraScale.Auth.Tools.TerraBaseDevMember;

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Api;
using TerraBase.Security;

/// <summary>
/// Boots a real TerraBase member for local ts-auth development and testing:
/// a Kestrel host over real sockets serving the public database API, with a
/// single dev API key standing in for the platform's project-key gateway.
/// Every env var has a working default, so a bare `dotnet run` is enough.
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        var url = Environment.GetEnvironmentVariable("TERRABASE_HOST_URL") ?? "http://127.0.0.1:8420";
        var apiKey = Environment.GetEnvironmentVariable("TERRABASE_DEV_API_KEY") ?? "dev";

        await using var app = TerraBaseHost.Create(url, builder =>
        {
            builder.Services.AddSingleton<IStartupFilter>(new DevKeyStartupFilter(apiKey));
            builder.Services.AddSingleton<ITenantAuthorizationHook, AllowAllAuthorizationHook>();
            SetDefault(builder, "TERRABASE_REGION", "GRU");
            SetDefault(builder, "TERRABASE_DATABASE_ROUTE", "ts-auth-dev");
            SetDefault(builder, "TERRABASE_DATABASE_TENANT", "ts-auth");
            SetDefault(builder, "TERRABASE_DATABASE_ULID", "01ARZ3NDEKTSV4RRFFQ69G5FAV");
            SetDefault(builder, "TERRABASE_DATABASE_MODE", "explicit");
            SetDefault(builder, "TERRABASE_DATABASE_PATH", Path.Combine(Path.GetTempPath(), "ts-auth-dev"));
            SetDefault(builder, "TERRABASE_DATABASE_HOME_REGION", "GRU");
            SetDefault(builder, "TERRABASE_DATABASE_SELECTED_REGIONS", "GRU");
            SetDefault(builder, "TERRABASE_ALLOW_SCHEMALESS", "true");
        });
        await app.RunAsync();
    }

    /// <summary>Configuration[key] = value unless the operator already set the env var.</summary>
    private static void SetDefault(WebApplicationBuilder builder, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration[key]))
            builder.Configuration[key] = value;
    }

    /// <summary>Authenticates `Authorization: Bearer &lt;key&gt;` against the dev key and
    /// installs the matching principal — the shape the platform gateway provides
    /// in production. Everything else stays the host's default fail-closed.</summary>
    private sealed class DevKeyStartupFilter(string apiKey) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
        {
            application.Use(async (context, continuation) =>
            {
                var header = context.Request.Headers.Authorization.ToString();
                var presented = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : string.Empty;
                if (!CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(presented),
                        Encoding.UTF8.GetBytes(apiKey)))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsync("unauthorized");
                    return;
                }
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "ts-auth-dev")],
                    "ts-auth-dev"));
                await continuation().ConfigureAwait(false);
            });
            next(application);
        };
    }

    /// <summary>Local development only: every authenticated principal may use the
    /// configured database. A real deployment plugs the platform's authorization.</summary>
    private sealed class AllowAllAuthorizationHook : ITenantAuthorizationHook
    {
        public ValueTask<TenantAuthorizationDecision> AuthorizeAsync(
            TenantAuthorizationRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(TenantAuthorizationDecision.AllowUntil(DateTimeOffset.UtcNow.AddSeconds(10)));
    }
}
