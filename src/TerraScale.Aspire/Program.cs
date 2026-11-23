using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var rawInstance = Environment.GetEnvironmentVariable("TS_DEV_INSTANCE") ?? "0";
if (!int.TryParse(rawInstance, NumberStyles.None, CultureInfo.InvariantCulture, out var instance) || instance < 0)
{
    throw new InvalidOperationException($"TS_DEV_INSTANCE must be a non-negative integer, got '{rawInstance}'");
}

var ports = new DevPorts(instance);
var suffix = instance == 0 ? string.Empty : $"-{instance}";
var stateDirectory = Path.Combine(Path.GetTempPath(), $"terrascale-aspire{suffix}");
if (Directory.Exists(stateDirectory))
{
    Directory.Delete(stateDirectory, recursive: true);
}

Directory.CreateDirectory(stateDirectory);

MeshNode[] mesh = [new("gru", "gru-1", 0, ports), new("sjc", "sjc-1", 1, ports), new("fra", "fra-1", 2, ports)];
var gru = mesh[0];
var peersFile = Path.Combine(stateDirectory, "peers.json");
using (var stream = File.Create(peersFile))
{
    using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
    writer.WriteStartArray();
    foreach (var node in mesh)
    {
        writer.WriteStartObject();
        writer.WriteString("nodeId", node.NodeId);
        writer.WriteString("region", node.Region);
        writer.WriteString("host", "127.0.0.1");
        writer.WriteNumber("quicPort", node.Quic);
        writer.WriteNumber("internalHttpPort", node.Internal);
        writer.WriteEndObject();
    }

    writer.WriteEndArray();
}

var peerSecret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
var terrabaseMasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
var authMasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
var dashboardClientSecret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
var sessionEncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
var stripeSecretKey = "sk_test_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
var stripeWebhookSecret = "whsec_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

const string Audience = "https://api.terrascale.tech";
var authPort = ports.Of(8090);
var authMetricsPort = ports.Of(9092);
var platformPort = ports.Of(8082);
var platformMetricsPort = ports.Of(9093);
var dashboardPort = ports.Of(8070);
var gruInternalUrl = $"http://127.0.0.1:{gru.Internal}";
var authPublicOrigin = $"http://localhost:{authPort}";
var platformUrl = $"http://127.0.0.1:{platformPort}";
var meshAuthIssuer = $"https://localhost:{authPort}/terrascale";
var platformAuthIssuer = $"{authPublicOrigin}/terrascale";
var gruQuic = $"{gru.NodeId}.{gru.Region}@127.0.0.1:{gru.Quic}";
var authListen = $"http://127.0.0.1:{authPort}";
var authMetricsListen = $"http://127.0.0.1:{authMetricsPort}";
var dashboardListen = $"http://127.0.0.1:{dashboardPort}";
var dashboardRedirectUri = $"http://localhost:{dashboardPort}/login/callback";
var dashboardPostLogoutUri = $"http://localhost:{dashboardPort}/login";

var terrabase = mesh.Select(node => builder.AddDotnetProject($"terrabase-{node.Region}", "../TerraBase/TerraBase.csproj")
        .WithEnvironment("DOTNET_ENVIRONMENT", "Production")
        .WithEnvironment("TS_REGION", node.Region)
        .WithEnvironment("TS_NODE", node.NodeId)
        .WithEnvironment("TS_PEERS_FILE", peersFile)
        .WithEnvironment("TERRABASE_DATA_DIRECTORY", Path.Combine(stateDirectory, $"terrabase-{node.Region}"))
        .WithEnvironment("TERRABASE_PORT", DevPorts.Text(node.Http))
        .WithEnvironment("TERRABASE_INTERNAL_PORT", DevPorts.Text(node.Internal))
        .WithEnvironment("TERRABASE_METRICS_PORT", DevPorts.Text(node.Metrics))
        .WithEnvironment("TERRABASE_POSTGRES_PORT", DevPorts.Text(node.Postgres))
        .WithEnvironment("TERRABASE_QUIC_PORT", DevPorts.Text(node.Quic))
        .WithEnvironment("TERRABASE_PEER_SECRET", peerSecret)
        .WithEnvironment("TERRABASE_MASTER_KEY", terrabaseMasterKey)
        .WithEnvironment("TERRABASE_AUTH_ISSUER", meshAuthIssuer)
        .WithEnvironment("TERRABASE_AUTH_AUDIENCE", Audience)
        .WithHttpEndpoint(port: node.Http, targetPort: node.Http, name: "http", isProxied: false)
        .WithHttpEndpoint(port: node.Internal, targetPort: node.Internal, name: "internal", isProxied: false)
        .WithHttpEndpoint(port: node.Metrics, targetPort: node.Metrics, name: "metrics", isProxied: false)
        .WithEndpoint(port: node.Postgres, targetPort: node.Postgres, scheme: "tcp", name: "postgres", isProxied: false)
        .WithHttpHealthCheck("/readyz", endpointName: "http"))
    .ToArray();
var terrabaseGru = terrabase[0];

var platform = builder.AddDotnetProject("ts-platform", "../TerraScale.Platform/TerraScale.Platform.csproj")
    .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
    .WithEnvironment("TS_REGION", gru.Region)
    .WithEnvironment("TS_NODE", "platform-gru")
    .WithEnvironment("TS_PLATFORM_PORT", DevPorts.Text(platformPort))
    .WithEnvironment("TS_PLATFORM_METRICS_PORT", DevPorts.Text(platformMetricsPort))
    .WithEnvironment("TS_PLATFORM_TERRABASE_URL", gruInternalUrl)
    .WithEnvironment("TS_PLATFORM_TERRABASE_QUIC", gruQuic)
    .WithEnvironment("TERRABASE_PEER_SECRET", peerSecret)
    .WithEnvironment("TERRABASE_MASTER_KEY", terrabaseMasterKey)
    .WithEnvironment("TERRABASE_AUTH_ISSUER", platformAuthIssuer)
    .WithEnvironment("TERRABASE_AUTH_AUDIENCE", Audience)
    .WithHttpEndpoint(port: platformPort, targetPort: platformPort, name: "http", isProxied: false)
    .WithHttpEndpoint(port: platformMetricsPort, targetPort: platformMetricsPort, name: "metrics", isProxied: false)
    .WithHttpHealthCheck("/readyz", endpointName: "http")
    .WaitFor(terrabaseGru);

var auth = builder.AddDotnetProject("ts-auth", "../TerraScale.Authentication/TerraScale.Authentication.csproj")
    .WithEnvironment("TS_AUTH_ENVIRONMENT", "Development")
    .WithEnvironment("TS_AUTH_MASTER_KEY", authMasterKey)
    .WithEnvironment("TERRABASE_PEER_SECRET", peerSecret)
    .WithEnvironment("TS_AUTH_TERRABASE_URL", gruInternalUrl)
    .WithEnvironment("TS_AUTH_LISTEN", authListen)
    .WithEnvironment("TS_AUTH_METRICS_LISTEN", authMetricsListen)
    .WithEnvironment("TS_AUTH_PUBLIC_ORIGIN", authPublicOrigin)
    .WithEnvironment("TS_AUTH_DASHBOARD_CLIENT_SECRET", dashboardClientSecret)
    .WithEnvironment("TS_AUTH_DASHBOARD_REDIRECT_URIS", dashboardRedirectUri)
    .WithEnvironment("TS_AUTH_DASHBOARD_POST_LOGOUT_REDIRECT_URIS", dashboardPostLogoutUri)
    .WithHttpEndpoint(port: authPort, targetPort: authPort, name: "http", isProxied: false)
    .WithHttpEndpoint(port: authMetricsPort, targetPort: authMetricsPort, name: "metrics", isProxied: false)
    .WithHttpHealthCheck("/readyz", endpointName: "http")
    .WaitFor(terrabaseGru);

var dashboard = builder.AddDotnetProject("dashboard", "../TerraScale.Dashboard/TerraScale.Dashboard.csproj")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("ASPNETCORE_URLS", dashboardListen)
    .WithEnvironment("TS_REGION", gru.Region)
    .WithEnvironment("TERRABASE_PEER_SECRET", peerSecret)
    .WithEnvironment("TS_AUTH_DASHBOARD_CLIENT_SECRET", dashboardClientSecret)
    .WithEnvironment("TS_DASHBOARD_SESSION_ENCRYPTION_KEY", sessionEncryptionKey)
    .WithEnvironment("STRIPE_SECRET_KEY", stripeSecretKey)
    .WithEnvironment("STRIPE_WEBHOOK_SECRET", stripeWebhookSecret)
    .WithEnvironment("TS_DASHBOARD_TERRABASE_URL", gruInternalUrl)
    .WithEnvironment("TS_DASHBOARD_PLATFORM_URL", platformUrl)
    .WithHttpEndpoint(port: dashboardPort, targetPort: dashboardPort, name: "http", isProxied: false)
    .WithHttpHealthCheck("/healthz", endpointName: "http")
    .WaitFor(terrabaseGru)
    .WaitFor(platform)
    .WaitFor(auth);

builder.AddExecutable("dashboard-css", "node", "../TerraScale.Dashboard/web", "build/build-css.mjs", "--watch", "--dev")
    .WaitFor(dashboard);

builder.AddExecutable("dashboard-js", "node", "../TerraScale.Dashboard/web", "build/build-js.mjs", "--watch")
    .WaitFor(dashboard);

await builder.Build().RunAsync();

internal sealed record DevPorts(int Instance)
{
    public int Of(int basePort) => basePort + (100 * Instance);

    public static string Text(int port) => port.ToString(CultureInfo.InvariantCulture);
}

internal sealed record MeshNode(string Region, string NodeId, int Index, DevPorts Ports)
{
    public int Http => Ports.Of(8080 + (3 * Index));

    public int Internal => Ports.Of(8081 + (3 * Index));

    public int Metrics => Ports.Of(9091 + (3 * Index));

    public int Quic => Ports.Of(8443 + Index);

    public int Postgres => Ports.Of(5432 + Index);
}
