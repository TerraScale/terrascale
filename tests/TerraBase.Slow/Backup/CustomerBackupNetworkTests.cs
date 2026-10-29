using System.Net;
using System.Text.Json;
using TerraBase.Testing;
using static TerraBase.Testing.ManagementApi;

namespace TerraBase.Slow.Backup;

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
        var s3 = new FakeS3();
        await using var api = await StartAsync(s3.Configure, services => network.Configure(s3, services));
        var database = await api.CreateManagedDatabaseAsync();

        using var response = await SaveAsync(api, database, network.Endpoint, cancellationToken);

        await UnavailableAsync(response, cancellationToken);
        await Assert.That(network.Resolutions).IsGreaterThan(0);
        await Assert.That(network.Connections).IsEqualTo(0);
    }

    [Test]
    [Arguments("10.0.0.1")]
    [Arguments("172.16.0.1")]
    [Arguments("192.168.0.1")]
    [Arguments("169.254.169.254")]
    [Arguments("127.0.0.1")]
    [Arguments("100.64.0.1")]
    [Arguments("198.18.0.1")]
    [Arguments("192.0.0.1")]
    [Arguments("192.0.2.1")]
    [Arguments("192.88.99.2")]
    [Arguments("198.51.100.1")]
    [Arguments("203.0.113.1")]
    [Arguments("::1")]
    [Arguments("fc00::1")]
    [Arguments("fe80::1")]
    [Arguments("::ffff:127.0.0.1")]
    [Arguments("::ffff:10.0.0.1")]
    [Arguments("::ffff:192.0.0.1")]
    [Arguments("64:ff9b::a00:1")]
    [Arguments("2002:7f00:1::")]
    [Arguments("2001:0:4136:e378:8000:63bf:3fff:fdd2")]
    [Arguments("2001:2::1")]
    [Arguments("2001:db8::1")]
    [Arguments("2001:5::1")]
    [Arguments("3fff::1")]
    public async Task Mixed_public_and_forbidden_DNS_answers_never_reach_the_connector(string forbidden)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var network = await CustomerBackupNetworkFixture.StartAsync(cancellationToken);
        network.Addresses("93.184.216.34", forbidden);
        var s3 = new FakeS3();
        await using var api = await StartAsync(s3.Configure, services => network.Configure(s3, services));
        var database = await api.CreateManagedDatabaseAsync();

        using var response = await SaveAsync(api, database, network.Endpoint, cancellationToken);

        await UnavailableAsync(response, cancellationToken);
        await Assert.That(network.Resolutions).IsGreaterThan(0);
        await Assert.That(network.Connections).IsEqualTo(0);
        await Assert.That(network.Puts).IsEqualTo(0);
    }

    [Test]
    [Arguments("https://127.0.0.1")]
    [Arguments("https://169.254.169.254")]
    [Arguments("https://[::ffff:127.0.0.1]")]
    [Arguments("https://[::ffff:10.0.0.1]")]
    [Arguments("https://[::ffff:192.0.0.1]")]
    [Arguments("https://[64:ff9b::a00:1]")]
    [Arguments("https://192.0.0.1")]
    [Arguments("https://192.0.2.1")]
    [Arguments("https://192.88.99.2")]
    [Arguments("https://198.51.100.1")]
    [Arguments("https://203.0.113.1")]
    [Arguments("https://[2001:2::1]")]
    [Arguments("https://[2001:db8::1]")]
    [Arguments("https://[2001:5::1]")]
    [Arguments("https://[3fff::1]")]
    public async Task Forbidden_literals_are_rejected_before_DNS_or_connect(string endpoint)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var network = await CustomerBackupNetworkFixture.StartAsync(cancellationToken);
        var s3 = new FakeS3();
        await using var api = await StartAsync(s3.Configure, services => network.Configure(s3, services));
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
    [Arguments("192.0.0.9")]
    [Arguments("192.0.0.10")]
    [Arguments("2001:1::1")]
    [Arguments("2001:1::2")]
    [Arguments("2001:1::3")]
    [Arguments("2001:3::1")]
    [Arguments("2001:4:112::1")]
    [Arguments("2001:20::1")]
    [Arguments("2001:30::1")]
    public async Task Public_answers_complete_the_real_TLS_probe_without_proxy_routing(string address)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var network = await CustomerBackupNetworkFixture.StartAsync(cancellationToken);
        network.Addresses(address, "93.184.216.35");
        var s3 = new FakeS3();
        await using var api = await StartAsync(s3.Configure, services => network.Configure(s3, services));
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
    }

    [Test]
    [Arguments("https://192.0.0.9")]
    [Arguments("https://192.0.0.10")]
    [Arguments("https://[2001:1::3]")]
    [Arguments("https://[2001:3::1]")]
    [Arguments("https://[2001:4:112::1]")]
    [Arguments("https://[2001:20::1]")]
    [Arguments("https://[2001:30::1]")]
    public async Task Globally_reachable_special_purpose_literals_pass_validation_before_the_fixture_refuses_the_host(string endpoint)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var network = await CustomerBackupNetworkFixture.StartAsync(cancellationToken);
        var s3 = new FakeS3();
        await using var api = await StartAsync(s3.Configure, services => network.Configure(s3, services));
        var database = await api.CreateManagedDatabaseAsync();

        // This DNS fixture accepts only its TLS listener's hostname, so a validated literal stops at resolution.
        using var response = await SaveAsync(api, database, new Uri(endpoint), cancellationToken);

        await UnavailableAsync(response, cancellationToken);
        await Assert.That(network.Resolutions).IsGreaterThan(0);
        await Assert.That(network.Connections).IsEqualTo(0);
    }

    [Test]
    public async Task Enabling_proxy_routing_is_blocked_before_a_customer_connection()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var network = await CustomerBackupNetworkFixture.StartAsync(cancellationToken);
        var s3 = new FakeS3();
        await using var api = await StartAsync(s3.Configure, services => network.Configure(s3, services, enableProxy: true));
        var database = await api.CreateManagedDatabaseAsync();

        using var response = await SaveAsync(api, database, network.Endpoint, cancellationToken);

        await UnavailableAsync(response, cancellationToken);
        await Assert.That(network.ProxyRoutingLookups).IsGreaterThan(0);
        await Assert.That(network.Resolutions).IsGreaterThan(0);
        await Assert.That(network.Connections).IsEqualTo(0);
        await Assert.That(network.Puts).IsEqualTo(0);
        using var configuration = await api.Client.GetAsync(Uri(DestinationPath(database)), cancellationToken);
        await Assert.That(configuration.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task DNS_rebinding_after_syntactic_validation_is_rechecked_at_connect_time()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var network = await CustomerBackupNetworkFixture.StartAsync(cancellationToken);
        network.HoldResolution = true;
        var s3 = new FakeS3();
        await using var api = await StartAsync(s3.Configure, services => network.Configure(s3, services));
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
    }

    [Test]
    public async Task Redirects_from_a_public_TLS_endpoint_are_not_followed()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var network = await CustomerBackupNetworkFixture.StartAsync(cancellationToken);
        network.Redirect = true;
        var s3 = new FakeS3();
        await using var api = await StartAsync(s3.Configure, services => network.Configure(s3, services));
        var database = await api.CreateManagedDatabaseAsync();

        using var response = await SaveAsync(api, database, network.Endpoint, cancellationToken);

        await UnavailableAsync(response, cancellationToken);
        await Assert.That(network.Connections).IsGreaterThan(0);
        await Assert.That(network.RedirectsFollowed).IsEqualTo(0);
        await Assert.That(network.ProductionUsesProxy).IsFalse();
        await Assert.That(network.ProxyRoutingLookups).IsEqualTo(0);
    }

    [Test]
    public async Task Canceling_a_listener_request_cancels_the_real_handler_connection()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var network = await CustomerBackupNetworkFixture.StartAsync(cancellationToken);
        network.HoldConnect = true;
        var s3 = new FakeS3();
        await using var api = await StartAsync(s3.Configure, services => network.Configure(s3, services));
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
            bucket = FakeS3.CustomerBucket,
            accessKeyId = FakeS3.CustomerAccessKey,
            secretKey = FakeS3.CustomerSecretKey,
            mode = "customer",
        }, cancellationToken);

    private static async Task UnavailableAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        await Assert.That(json.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("unavailable");
        await Assert.That(json.GetProperty("error").GetProperty("message").GetString())
            .IsEqualTo("customer backup bucket could not be accessed");
    }
}
