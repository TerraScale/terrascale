using System.Globalization;
using System.Security.Cryptography;
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var rawInstance = Environment.GetEnvironmentVariable("TS_DEV_INSTANCE") ?? "0";
if (!int.TryParse(rawInstance, NumberStyles.None, CultureInfo.InvariantCulture, out var instance) || instance < 0)
{
    throw new InvalidOperationException($"TS_DEV_INSTANCE must be a non-negative integer, got '{rawInstance}'");
}

int Port(int basePort) => basePort + (100 * instance);

var suffix = instance == 0 ? string.Empty : $"-{instance}";
var dataDirectory = Path.Combine(Path.GetTempPath(), $"terrascale-aspire-terrabase{suffix}");
if (Directory.Exists(dataDirectory))
{
    Directory.Delete(dataDirectory, recursive: true);
}

Directory.CreateDirectory(dataDirectory);

var peerSecret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
var authMasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
var dashboardClientSecret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
var sessionEncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
var stripeSecretKey = "sk_test_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
var stripeWebhookSecret = "whsec_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

var terrabaseInternalUrl = $"http://127.0.0.1:{Port(8081)}";
var authListen = $"http://127.0.0.1:{Port(8090)}";
var authMetricsListen = $"http://127.0.0.1:{Port(9092)}";
var authPublicOrigin = $"http://localhost:{Port(8090)}";
var dashboardListen = $"http://127.0.0.1:{Port(8070)}";
var dashboardRedirectUri = $"http://localhost:{Port(8070)}/login/callback";
var dashboardPostLogoutUri = $"http://localhost:{Port(8070)}/login";

var terrabase = builder.AddDotnetProject("terrabase", "../TerraBase/TerraBase.csproj")
    .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
    .WithEnvironment("TERRABASE_DATA_DIRECTORY", dataDirectory)
    .WithEnvironment("TERRABASE_PORT", Port(8080).ToString(CultureInfo.InvariantCulture))
    .WithEnvironment("TERRABASE_INTERNAL_PORT", Port(8081).ToString(CultureInfo.InvariantCulture))
    .WithEnvironment("TERRABASE_METRICS_PORT", Port(9091).ToString(CultureInfo.InvariantCulture))
    .WithEnvironment("TERRABASE_POSTGRES_PORT", Port(5432).ToString(CultureInfo.InvariantCulture))
    .WithEnvironment("TERRABASE_PEER_SECRET", peerSecret)
    .WithHttpEndpoint(port: Port(8080), targetPort: Port(8080), name: "http", isProxied: false)
    .WithHttpEndpoint(port: Port(8081), targetPort: Port(8081), name: "internal", isProxied: false)
    .WithHttpEndpoint(port: Port(9091), targetPort: Port(9091), name: "metrics", isProxied: false)
    .WithHttpHealthCheck("/readyz", endpointName: "http");

var auth = builder.AddDotnetProject("auth", "../TerraScale.Authentication/TerraScale.Authentication.csproj")
    .WithEnvironment("TS_AUTH_ENVIRONMENT", "Development")
    .WithEnvironment("TS_AUTH_MASTER_KEY", authMasterKey)
    .WithEnvironment("TERRABASE_PEER_SECRET", peerSecret)
    .WithEnvironment("TS_AUTH_TERRABASE_URL", terrabaseInternalUrl)
    .WithEnvironment("TS_AUTH_LISTEN", authListen)
    .WithEnvironment("TS_AUTH_METRICS_LISTEN", authMetricsListen)
    .WithEnvironment("TS_AUTH_PUBLIC_ORIGIN", authPublicOrigin)
    .WithEnvironment("TS_AUTH_DASHBOARD_CLIENT_SECRET", dashboardClientSecret)
    .WithEnvironment("TS_AUTH_DASHBOARD_REDIRECT_URIS", dashboardRedirectUri)
    .WithEnvironment("TS_AUTH_DASHBOARD_POST_LOGOUT_REDIRECT_URIS", dashboardPostLogoutUri)
    .WithHttpEndpoint(port: Port(8090), targetPort: Port(8090), name: "http", isProxied: false)
    .WithHttpEndpoint(port: Port(9092), targetPort: Port(9092), name: "metrics", isProxied: false)
    .WithHttpHealthCheck("/readyz", endpointName: "http")
    .WaitFor(terrabase);

builder.AddDotnetProject("dashboard", "../TerraScale.Dashboard/TerraScale.Dashboard.csproj")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("ASPNETCORE_URLS", dashboardListen)
    .WithEnvironment("TERRABASE_PEER_SECRET", peerSecret)
    .WithEnvironment("TS_AUTH_DASHBOARD_CLIENT_SECRET", dashboardClientSecret)
    .WithEnvironment("TS_DASHBOARD_SESSION_ENCRYPTION_KEY", sessionEncryptionKey)
    .WithEnvironment("STRIPE_SECRET_KEY", stripeSecretKey)
    .WithEnvironment("STRIPE_WEBHOOK_SECRET", stripeWebhookSecret)
    .WithEnvironment("TS_DASHBOARD_TERRABASE_URL", terrabaseInternalUrl)
    .WithHttpEndpoint(port: Port(8070), targetPort: Port(8070), name: "http", isProxied: false)
    .WithHttpHealthCheck("/healthz", endpointName: "http")
    .WaitFor(terrabase)
    .WaitFor(auth);

await builder.Build().RunAsync().ConfigureAwait(false);
