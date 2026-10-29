using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

/// <summary>Construction and token-provider validation; these need no server.</summary>
public sealed class TerraBaseClientOptionsTests
{
    private static readonly Uri Origin = new("https://terrabase.example.test:8443/");
    private static readonly Uri Private = new("http://100.64.0.1:8081/");

    [Test]
    public async Task RecommendedHandlerDisablesRedirectsAndCookies()
    {
        using var handler = TerraBaseHttp.CreateHandler();
        await Assert.That(handler.AllowAutoRedirect).IsFalse();
        await Assert.That(handler.UseCookies).IsFalse();
        await Assert.That(handler.ConnectTimeout).IsEqualTo(TimeSpan.FromSeconds(10));
        await Assert.That(handler.PooledConnectionLifetime).IsEqualTo(TimeSpan.FromMinutes(2));
    }

    [Test]
    [Arguments("http://terrabase.example.test/")]
    [Arguments("https://terrabase.example.test/v1/")]
    [Arguments("https://user:secret@terrabase.example.test/")]
    [Arguments("https://terrabase.example.test/?a=b")]
    public async Task BaseAddressMustBeAnHttpsOrigin(string address)
    {
        using var http = new HttpClient();
        await Assert.That(() => new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions { BaseAddress = new Uri(address), ApiKey = "tb_key" }))
            .Throws<ArgumentException>();
    }

    [Test]
    [Arguments("http://100.64.0.0:8081/")]
    [Arguments("http://100.64.0.1:8081/")]
    [Arguments("http://100.100.10.20:8081/")]
    [Arguments("http://100.127.255.255:8081/")]
    [Arguments("http://localhost:8081/")]
    [Arguments("http://127.0.0.1:8081/")]
    [Arguments("http://127.200.3.4:8081/")]
    [Arguments("http://[::1]:8081/")]
    public async Task PrivateListenersAcceptPlainHttp(string address)
    {
        using var http = new HttpClient();
        var resolver = new TerraBaseStaticEndpointResolver(new Uri(address));
        await Assert.That(await resolver.ResolveAsync()).IsEqualTo(new Uri(address));
        _ = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions { BaseAddress = new Uri(address) });
        _ = new TerraBaseManagementClient(http, new TerraBaseManagementClientOptions { BaseAddress = new Uri(address) });
    }

    [Test]
    [Arguments("100.63.255.255")]
    [Arguments("100.128.0.0")]
    [Arguments("8.8.8.8")]
    [Arguments("10.0.0.1")]
    [Arguments("172.16.0.1")]
    [Arguments("192.168.0.1")]
    [Arguments("[2001:db8::1]")]
    [Arguments("[::ffff:100.64.0.1]")]
    [Arguments("terrabase.example.internal")]
    [Arguments("100.64.0.1.example.test")]
    [Arguments("127.0.0.1.example.test")]
    public async Task OtherOriginsRequireHttps(string host)
    {
        using var http = new HttpClient();
        var insecure = new Uri($"http://{host}:8081/");
        await Assert.That(() => new TerraBaseStaticEndpointResolver(insecure)).Throws<ArgumentException>();
        await Assert.That(() => new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions { BaseAddress = insecure, ApiKey = "tb_key" }))
            .Throws<ArgumentException>();
        await Assert.That(() => new TerraBaseManagementClient(http, new TerraBaseManagementClientOptions { BaseAddress = insecure }))
            .Throws<ArgumentException>();
        var secure = new Uri($"https://{host}:8443/");
        _ = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions { BaseAddress = secure, ApiKey = "tb_key" });
        _ = new TerraBaseManagementClient(http, new TerraBaseManagementClientOptions { BaseAddress = secure, AccessTokenProvider = _ => ValueTask.FromResult("token") });
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("tb key")]
    [Arguments("tb_key\n")]
    public async Task SecretsMustBePrintableTokens(string? secret)
    {
        using var http = new HttpClient();
        await Assert.That(() => new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions { BaseAddress = Origin, ApiKey = secret }))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task InternalTransportTestBoundsRemainValidated()
    {
        using var http = new HttpClient();
        await Assert.That(() => new TerraBaseConnection(http, Origin, null, "tb_key", "credential", TimeSpan.Zero, TerraBaseConnection.DefaultMaxResponseBytes))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new TerraBaseConnection(http, Origin, null, "tb_key", "credential", TimeSpan.FromHours(1), TerraBaseConnection.DefaultMaxResponseBytes))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new TerraBaseConnection(http, Private, null, null, "credential", TerraBaseConnection.RequestDeadline, 1024, anonymousAllowed: true))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task OptionsAndInvitationsNeverPrintSecrets()
    {
        var documents = new TerraBaseDocumentsClientOptions { BaseAddress = Origin, ApiKey = "tb_secretvalue" };
        var invitation = new TerraBaseCreatedInvitation(
            new TerraBaseInvitation("i", "a@example.com", TerraBaseRole.Member, "u", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "pending"), "org.inv.secretvalue");

        await Assert.That(documents.ToString()).DoesNotContain("secretvalue");
        await Assert.That(invitation.ToString()).DoesNotContain("secretvalue");
    }

    [Test]
    public async Task PathSegmentsAndVersionsAreValidated()
    {
        using var http = new HttpClient();
        var documents = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions { BaseAddress = Origin, ApiKey = "tb_key" });

        await Assert.That(() => documents.Database("..", "app")).Throws<ArgumentException>();
        await Assert.That(() => documents.Database("project", "")).Throws<ArgumentException>();
        await Assert.That(() => TerraBasePrecondition.Matches("bad\"version")).Throws<ArgumentException>();
        await Assert.That(() => TerraBaseTransactionOperation.AssertCount("items")).Throws<ArgumentException>();
    }

    [Test]
    [Arguments("id\r\nX-Injected: value")]
    [Arguments("id\0")]
    [Arguments("id\t")]
    public async Task OrganizationHeadersRejectControlsBeforeDispatch(string organizationId)
    {
        using var http = new HttpClient();
        var resolver = new CountingResolver(Origin);
        var management = new TerraBaseManagementClient(http, new TerraBaseManagementClientOptions { EndpointResolver = resolver, AccessTokenProvider = _ => ValueTask.FromResult("token") });

        await Assert.That(async () => { await management.GetOrganizationAsync("user", organizationId); }).Throws<ArgumentException>();
        await Assert.That(() => management.DeleteOrganizationAsync("user", organizationId)).Throws<ArgumentException>();
        await Assert.That(resolver.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task AccessTokenOptionsAreExclusiveForBothClients()
    {
        using var http = new HttpClient();
        Func<CancellationToken, ValueTask<string>> provider = _ => ValueTask.FromResult("tb_key");
        foreach (var secret in new[] { "", "tb_key" })
        {
            await Assert.That(() => new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions
            {
                BaseAddress = Origin,
                ApiKey = secret,
                AccessTokenProvider = provider,
            })).Throws<ArgumentException>();
        }

        await Assert.That(() => new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions { BaseAddress = Origin }))
            .Throws<ArgumentException>();
        await Assert.That(() => new TerraBaseManagementClient(http, new TerraBaseManagementClientOptions { BaseAddress = Origin }))
            .Throws<ArgumentException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InvalidProviderTokenStopsBeforeDispatch(bool managementClient)
    {
        using var http = new HttpClient();
        foreach (var secret in new string?[] { null, "", " ", "invalid token", "tb_key\n", "tb_key\0", "tb_key\u00a0", new string('x', 4097) })
        {
            var calls = 0;
            var request = ProviderRequest(http, managementClient, _ => { calls++; return ValueTask.FromResult(secret!); });
            await Assert.That(calls).IsEqualTo(0);
            await Assert.That(async () => { await request(default); }).Throws<ArgumentException>();
            await Assert.That(calls).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task TokenAcquisitionSharesDeadlineAndCallerCancellation(bool managementClient, bool callerCancels)
    {
        using var http = new HttpClient();
        using var caller = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var send = ProviderRequest(http, managementClient, async token =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "tb_key";
        }, callerCancels ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(100));
        var request = send(caller.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (callerCancels)
        {
            await caller.CancelAsync();
            await Assert.That(async () => { await request.WaitAsync(TimeSpan.FromSeconds(5)); }).Throws<OperationCanceledException>();
        }
        else
        {
            var error = await Assert.ThrowsAsync<TimeoutException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
            await Assert.That(error!.InnerException is OperationCanceledException).IsTrue();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task IndependentRequestsAcquireTokensConcurrently(bool managementClient)
    {
        using var http = new HttpClient();
        using var caller = new CancellationTokenSource();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var send = ProviderRequest(http, managementClient, async token =>
        {
            if (Interlocked.Increment(ref calls) == 2) bothStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "tb_key";
        });
        var first = send(caller.Token);
        var second = send(caller.Token);
        try
        {
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await caller.CancelAsync();
            await Assert.That(async () => { await first.WaitAsync(TimeSpan.FromSeconds(5)); }).Throws<OperationCanceledException>();
            await Assert.That(async () => { await second.WaitAsync(TimeSpan.FromSeconds(5)); }).Throws<OperationCanceledException>();
        }
        await Assert.That(calls).IsEqualTo(2);
    }

    private static Func<CancellationToken, Task> ProviderRequest(HttpClient http, bool managementClient,
        Func<CancellationToken, ValueTask<string>> provider, TimeSpan? timeout = null)
    {
        if (managementClient)
        {
            var management = timeout is null
                ? new TerraBaseManagementClient(http, new TerraBaseManagementClientOptions { BaseAddress = Origin, AccessTokenProvider = provider })
                : new TerraBaseManagementClient(new TerraBaseConnection(http, Origin, null, null, "credential", timeout.Value,
                    TerraBaseConnection.DefaultMaxResponseBytes, provider, anonymousAllowed: true));
            return token => management.ListOrganizationsAsync("user", cancellationToken: token);
        }

        var client = timeout is null
            ? new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions { BaseAddress = Origin, AccessTokenProvider = provider })
            : new TerraBaseDocumentsClient(new TerraBaseConnection(http, Origin, null, null, "credential", timeout.Value,
                TerraBaseConnection.DefaultMaxResponseBytes, provider, anonymousAllowed: true));
        var database = client.Database("project", "app");
        return token => database.ListCollectionsAsync(cancellationToken: token);
    }

    [Test]
    public async Task ResolverOptionsAreExclusiveAndStaticOriginsAreValidated()
    {
        using var http = new HttpClient();
        var resolver = new TerraBaseStaticEndpointResolver(Origin);
        await Assert.That(await resolver.ResolveAsync()).IsEqualTo(Origin);
        await Assert.That(() => new TerraBaseStaticEndpointResolver(new Uri("http://terrabase.example.test/"))).Throws<ArgumentException>();
        await Assert.That(() => new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions { BaseAddress = Origin, EndpointResolver = resolver, ApiKey = "tb_key" }))
            .Throws<ArgumentException>();
        await Assert.That(() => new TerraBaseManagementClient(http, new TerraBaseManagementClientOptions { BaseAddress = Origin, EndpointResolver = resolver, AccessTokenProvider = _ => ValueTask.FromResult("token") }))
            .Throws<ArgumentException>();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.That(async () => { await resolver.ResolveAsync(canceled.Token); }).Throws<OperationCanceledException>();
    }

    internal sealed class CountingResolver(Uri origin) : ITerraBaseEndpointResolver
    {
        public int Calls { get; private set; }

        public ValueTask<Uri> ResolveAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(origin);
        }
    }
}
