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
                var peers = Path.Combine(fixture.directory, region + ".peers");
                await File.WriteAllTextAsync(peers, $"node-{region} {region} {addresses[region]}\nnode-{remote} {remote} {proxy.EndPoint.Address}\n");
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
                    observations = $"replication={status.GetRawText()}, catalog={gate.GetRawText()}";
                    return status.GetProperty("peers").EnumerateArray().Any(peer => peer.GetProperty("region").GetString() != region)
                        && gate.GetProperty("open").GetBoolean();
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
