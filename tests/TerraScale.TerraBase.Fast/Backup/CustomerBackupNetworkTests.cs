using System.Net;
using System.Text.Json;
using TerraBase.Testing;
using static TerraBase.Testing.ManagementApi;

namespace TerraBase.Fast.Backup;

public sealed class CustomerBackupNetworkTests
{
    [Test]
    [Arguments(0)]
    [Arguments(33)]
    public async Task Empty_or_oversized_DNS_answers_are_unavailable_without_connecting(int count)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var network = await CustomerBackupNetworkFixture.StartAsync(cancellationToken);
        network.Addresses([.. Enumerable.Repeat("93.184.216.34", count)]);
        using var oci = new FakeOci();
        await using var api = await StartAsync(oci.ConfigureCustomer, services => network.Configure(oci, services));
        var database = await api.CreateManagedDatabaseAsync();

        using var response = await SaveAsync(api, database, network.Endpoint, cancellationToken);

        await UnavailableAsync(response, cancellationToken);
        await Assert.That(network.Resolutions).IsGreaterThan(0);
        await Assert.That(network.Connections).IsEqualTo(0);
        await Assert.That(oci.Secrets.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("10.0.0.1")]
    [Arguments("172.16.0.1")]
    [Arguments("192.168.0.1")]
    [Arguments("169.254.169.254")]
    [Arguments("127.0.0.1")]
    [Arguments("100.64.0.1")]
    [Arguments("198.18.0.1")]
    [Arguments("::1")]
    [Arguments("fc00::1")]
    [Arguments("fe80::1")]
    [Arguments("::ffff:127.0.0.1")]
    [Arguments("::ffff:10.0.0.1")]
    [Arguments("2002:7f00:1::")]
    [Arguments("2001:0:4136:e378:8000:63bf:3fff:fdd2")]
    public async Task Mixed_public_and_forbidden_DNS_answers_never_reach_the_connector(string forbidden)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var network = await CustomerBackupNetworkFixture.StartAsync(cancellationToken);
        network.Addresses("93.184.216.34", forbidden);
        using var oci = new FakeOci();
        await using var api = await StartAsync(oci.ConfigureCustomer, services => network.Configure(oci, services));
        var database = await api.CreateManagedDatabaseAsync();

        using var response = await SaveAsync(api, database, network.Endpoint, cancellationToken);

        await UnavailableAsync(response, cancellationToken);
        await Assert.That(network.Resolutions).IsGreaterThan(0);
        await Assert.That(network.Connections).IsEqualTo(0);
        await Assert.That(network.Puts).IsEqualTo(0);
        await Assert.That(oci.Secrets.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("https://127.0.0.1")]
    [Arguments("https://169.254.169.254")]
    [Arguments("https://[::ffff:127.0.0.1]")]
    [Arguments("https://[::ffff:10.0.0.1]")]
    public async Task Forbidden_literals_are_rejected_before_DNS_or_connect(string endpoint)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var network = await CustomerBackupNetworkFixture.StartAsync(cancellationToken);
        using var oci = new FakeOci();
        await using var api = await StartAsync(oci.ConfigureCustomer, services => network.Configure(oci, services));
        var database = await api.CreateManagedDatabaseAsync();

        using var response = await SaveAsync(api, database, new Uri(endpoint), cancellationToken);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(network.Resolutions).IsEqualTo(0);
        await Assert.That(network.Connections).IsEqualTo(0);
    }

    [Test]
    [Arguments("93.184.216.34")]
    [Arguments("::ffff:93.184.216.34")]
    [Arguments("2606:4700:4700::1111")]
    public async Task Public_answers_complete_the_real_TLS_probe_without_proxy_routing(string address)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var network = await CustomerBackupNetworkFixture.StartAsync(cancellationToken);
        network.Addresses(address, "93.184.216.35");
        using var oci = new FakeOci();
        await using var api = await StartAsync(oci.ConfigureCustomer, services => network.Configure(oci, services));
        var database = await api.CreateManagedDatabaseAsync();

        using var response = await SaveAsync(api, database, network.Endpoint, cancellationToken);

        response.EnsureSuccessStatusCode();
        await Assert.That(network.Connections).IsGreaterThan(0);
        await Assert.That(network.ProductionUsesProxy).IsFalse();
        await Assert.That(network.ProxyRoutingLookups).IsEqualTo(0);
        await Assert.That(network.Puts).IsEqualTo(1);
        await Assert.That(network.Gets).IsEqualTo(1);
        await Assert.That(network.Lists).IsEqualTo(1);
        await Assert.That(network.Deletes).IsEqualTo(1);
        await Assert.That(oci.Secrets.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Enabling_proxy_routing_is_blocked_before_a_customer_connection()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var network = await CustomerBackupNetworkFixture.StartAsync(cancellationToken);
        using var oci = new FakeOci();
        await using var api = await StartAsync(oci.ConfigureCustomer, services => network.Configure(oci, services, enableProxy: true));
        var database = await api.CreateManagedDatabaseAsync();

        using var response = await SaveAsync(api, database, network.Endpoint, cancellationToken);

        await UnavailableAsync(response, cancellationToken);
        await Assert.That(network.ProxyRoutingLookups).IsGreaterThan(0);
        await Assert.That(network.Resolutions).IsGreaterThan(0);
        await Assert.That(network.Connections).IsEqualTo(0);
        await Assert.That(network.Puts).IsEqualTo(0);
        await Assert.That(oci.Secrets.Count).IsEqualTo(0);
        using var configuration = await api.Client.GetAsync(Uri(DestinationPath(database)), cancellationToken);
        await Assert.That(configuration.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task DNS_rebinding_after_syntactic_validation_is_rechecked_at_connect_time()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var network = await CustomerBackupNetworkFixture.StartAsync(cancellationToken);
        network.HoldResolution = true;
        using var oci = new FakeOci();
        await using var api = await StartAsync(oci.ConfigureCustomer, services => network.Configure(oci, services));
        var database = await api.CreateManagedDatabaseAsync();
        var pending = SaveAsync(api, database, network.Endpoint, cancellationToken);
        try
        {
            await network.Resolving.Task.WaitAsync(cancellationToken);
            // The listener accepted this hostname while its answer was public; it changed before connection.
            network.Addresses("169.254.169.254");
        }
        finally
        {
            network.ReleaseResolution();
        }

        using var response = await pending;
        await UnavailableAsync(response, cancellationToken);
        await Assert.That(network.Connections).IsEqualTo(0);
        await Assert.That(oci.Secrets.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Redirects_from_a_public_TLS_endpoint_are_not_followed()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var network = await CustomerBackupNetworkFixture.StartAsync(cancellationToken);
        network.Redirect = true;
        using var oci = new FakeOci();
        await using var api = await StartAsync(oci.ConfigureCustomer, services => network.Configure(oci, services));
        var database = await api.CreateManagedDatabaseAsync();

        using var response = await SaveAsync(api, database, network.Endpoint, cancellationToken);

        await UnavailableAsync(response, cancellationToken);
        await Assert.That(network.Connections).IsGreaterThan(0);
        await Assert.That(network.RedirectsFollowed).IsEqualTo(0);
        await Assert.That(network.ProductionUsesProxy).IsFalse();
        await Assert.That(network.ProxyRoutingLookups).IsEqualTo(0);
        await Assert.That(oci.Secrets.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Canceling_a_listener_request_cancels_the_real_handler_connection()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var network = await CustomerBackupNetworkFixture.StartAsync(cancellationToken);
        network.HoldConnect = true;
        using var oci = new FakeOci();
        await using var api = await StartAsync(oci.ConfigureCustomer, services => network.Configure(oci, services));
        var database = await api.CreateManagedDatabaseAsync();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = SaveAsync(api, database, network.Endpoint, caller.Token);
        try
        {
            await network.Connecting.Task.WaitAsync(cancellationToken);
            await caller.CancelAsync();
            var canceled = false;
            try
            {
                using var response = await pending;
            }
            catch (OperationCanceledException) when (caller.IsCancellationRequested)
            {
                canceled = true;
            }

            await Assert.That(canceled).IsTrue();
            await network.ConnectCanceled.Task.WaitAsync(cancellationToken);
            await Assert.That(network.Puts).IsEqualTo(0);
            await Assert.That(oci.Secrets.Count).IsEqualTo(0);
            using var configuration = await api.Client.GetAsync(Uri(DestinationPath(database)), cancellationToken);
            await Assert.That(configuration.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
        finally
        {
            await caller.CancelAsync();
            network.ReleaseConnect();
        }
    }

    private static string DestinationPath(ManagedDatabaseRef database) =>
        $"/v1/management/organizations/{database.Organization}/projects/{database.Project}/backup-destination";

    private static Task<HttpResponseMessage> SaveAsync(ManagementApi api, ManagedDatabaseRef database, Uri endpoint, CancellationToken cancellationToken) =>
        api.Client.PutAsJsonAsync(Uri(DestinationPath(database)), new
        {
            endpoint = endpoint.AbsoluteUri,
            region = "test-region-1",
            bucket = FakeOci.CustomerBucket,
            accessKeyId = FakeOci.CustomerAccessKey,
            secretKey = FakeOci.CustomerSecretKey,
            mode = "customer",
        }, cancellationToken);

    private static async Task UnavailableAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        await Assert.That(json.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("unavailable");
        await Assert.That(json.GetProperty("error").GetProperty("message").GetString())
            .IsEqualTo("customer backup bucket or OCI Vault could not be accessed");
    }
}
