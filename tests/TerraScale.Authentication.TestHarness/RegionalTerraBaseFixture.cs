using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Tests;

/// <summary>Two real TerraBase binaries with independent storage and controllable peer links; no simulated regional copies.</summary>
public sealed class RegionalTerraBaseFixture : IAsyncDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("auth-regions-").FullName;
    private readonly Dictionary<string, TerraBaseTestServer> servers = new(StringComparer.Ordinal);
    private readonly List<TcpRelay> links = [];
    private readonly CancellationTokenSource topologyLifetime = new();
    private readonly List<Task> topologyRenewals = [];
    private readonly string masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private RegionalAnchorBucket? bucket;
    private readonly string auditKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>The distinct regions in this test's cluster.</summary>
    public static IReadOnlyList<string> Regions { get; } = ["gru", "sjc"];

    /// <summary>The isolated tenant shared through real catalog replication.</summary>
    public TerraBaseTestTenant Tenant { get; private set; } = null!;

    /// <summary>The region's independently running binary.</summary>
    public TerraBaseTestServer this[string region] => this.servers[region];

    /// <summary>The region's local SDK view of the replicated auth database.</summary>
    public TerraBaseDatabase Database(string region) => this[region].InternalDocuments().Database(this.Tenant.Project.Id, "auth");

    /// <summary>Starts both regions, waits for catalog and peer admission, and provisions the replicated auth collections.</summary>
    public static async Task<RegionalTerraBaseFixture> StartAsync()
    {
        var fixture = new RegionalTerraBaseFixture();
        try
        {
            var trustFile = Path.Combine(fixture.directory, "bucket-ca.pem");
            fixture.bucket = await RegionalAnchorBucket.StartAsync(trustFile);
            var addresses = Regions.ToDictionary(region => region, _ => TcpRelay.RandomLoopback(), StringComparer.Ordinal);
            using var reservation = new TcpListener(addresses["gru"], 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            foreach (var region in Regions)
            {
                var remote = Regions.Single(other => other != region);
                var proxy = TcpRelay.Start(new Uri($"http://{addresses[remote]}:{port}/"), new IPEndPoint(TcpRelay.RandomLoopback(), port));
                fixture.links.Add(proxy);
                var nodeDirectory = Directory.CreateDirectory(Path.Combine(fixture.directory, region)).FullName;
                var peers = Path.Combine(nodeDirectory, "peers");
                await File.WriteAllTextAsync(peers, $"node-{region} {region} {addresses[region]}\nnode-{remote} {remote} {proxy.EndPoint.Address}\n");
                // The fixture owns admission independently of the service file, just as ts-orch does.
                object[] admitted = [
                    new { node = "node-" + region, region, address = addresses[region].ToString() },
                    new { node = "node-" + remote, region = remote, address = proxy.EndPoint.Address.ToString() },
                ];
                var manifest = Path.Combine(nodeDirectory, "topology.json");
                await WriteTopologyAsync(manifest, admitted, fixture.topologyLifetime.Token);
                fixture.topologyRenewals.Add(RenewTopologyAsync(manifest, admitted, fixture.topologyLifetime.Token));
                var (server, unavailable) = await TerraBaseTestServer.StartRegionalAsync(new RegionalNode(region, "node-" + region, addresses[region], port, peers, fixture.bucket.Address, trustFile, fixture.masterKey));
                fixture.servers.Add(region, server ?? throw new InvalidOperationException(unavailable));
            }

            foreach (var region in Regions)
            {
                var observations = string.Empty;
                if (!await ReadinessWait.UntilAsync(async () =>
                {
                    var status = await ReadAsync(fixture[region], "/replication");
                    var gate = await ReadAsync(fixture[region], "/v1/catalog-gate");
                    var topology = await ReadAsync(fixture[region], "/v1/health/topology");
                    observations = $"replication={status.GetRawText()}, catalog={gate.GetRawText()}, topology={topology.GetRawText()}";
                    return status.GetProperty("peers").EnumerateArray().Any(peer => peer.GetProperty("region").GetString() != region)
                        && gate.GetProperty("open").GetBoolean() && topology.GetProperty("mismatch").GetInt32() == 0;
                }, TimeSpan.FromSeconds(60)))
                {
                    throw new TimeoutException($"{region} did not connect to its peer and verify its catalog: {observations}");
                }
            }

            fixture.Tenant = await fixture["gru"].CreateTenantAsync();
            await fixture.Database("gru").CreateAsync();
            await new AuthStore(fixture.Database("gru"), TimeProvider.System).ProvisionAsync();
            foreach (var region in Regions)
            {
                if (!await ReadinessWait.UntilAsync(async () =>
                {
                    try
                    {
                        return (await fixture.Database(region).ListCollectionsAsync()).Items.Count == AuthStore.RequiredCollections.Count;
                    }
                    catch (TerraBaseException error) when (error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable or HttpStatusCode.MisdirectedRequest)
                    {
                        return false;
                    }
                }, TimeSpan.FromSeconds(30)))
                {
                    throw new TimeoutException($"{region} did not receive the auth collections.");
                }
            }

            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    /// <summary>Starts real ts-auth with all SDK operations going to this region's real local TerraBase.</summary>
    public Task<AuthTestServer> StartAuthAsync(string region, Action<WebApplication>? routes = null, CapturingEmailSender? email = null) => AuthTestServer.StartAsync(
        settings: settings =>
        {
            settings["master-key"] = this.masterKey;
            settings["audit-signing-key"] = this.auditKey;
            settings["dashboard-client-secret"] = OidcHarness.ClientSecret;
            settings["dashboard-redirect-uris"] = OidcHarness.RedirectUri;
            settings["access-token-audiences"] = "https://api.terrascale.tech";
        },
        services: services =>
        {
            services.AddSingleton<IEmailSender>(email ?? new CapturingEmailSender());
            services.AddSingleton(this[region].Http);
            services.AddSingleton(this.Database(region));
            services.AddKeyedSingleton(App.StorageAuthority, (_, _) => this[region].Management());
        },
        routes: routes,
        configure: builder => builder.Configuration["TS_REGION"] = region,
        beforeStart: services => SigningKeyProvisioning.ProvisionAsync(services, CancellationToken.None));

    /// <summary>Delays both peer links without redirecting client operations or replacing either backend.</summary>
    public void HoldReplication()
    {
        foreach (var link in this.links)
        {
            link.Hold();
        }
    }

    /// <summary>Releases delayed peer traffic and subsequent replication.</summary>
    public void ReleaseReplication()
    {
        foreach (var link in this.links)
        {
            link.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await this.topologyLifetime.CancelAsync();
        try
        {
            await Task.WhenAll(this.topologyRenewals);
        }
        catch (OperationCanceledException) when (this.topologyLifetime.IsCancellationRequested)
        {
            // Stop the fixture's admission leases before deleting their files.
        }
        this.topologyLifetime.Dispose();
        this.ReleaseReplication();
        foreach (var server in this.servers.Values)
        {
            await server.DisposeAsync();
        }

        foreach (var link in this.links)
        {
            await link.DisposeAsync();
        }

        if (this.bucket is not null)
        {
            await this.bucket.DisposeAsync();
        }

        Directory.Delete(this.directory, recursive: true);
    }

    private static async Task RenewTopologyAsync(string path, object[] peers, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await WriteTopologyAsync(path, peers, cancellationToken);
        }
    }

    private static async Task WriteTopologyAsync(string path, object[] peers, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(path + ".next", JsonSerializer.Serialize(new
        {
            generation = 1L,
            observedAt = DateTimeOffset.UtcNow,
            regions = Regions,
            peers,
        }), cancellationToken);
        File.Move(path + ".next", path, overwrite: true);
    }

    private static async Task<JsonElement> ReadAsync(TerraBaseTestServer server, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server.InternalAddress, path))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using var response = await server.Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
