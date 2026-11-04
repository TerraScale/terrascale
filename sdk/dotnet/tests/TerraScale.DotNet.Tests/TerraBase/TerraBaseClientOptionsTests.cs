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
    public async Task OptionsAndInvitationsNeverPrintSecrets()
    {
        var options = new TerraBaseDocumentsClientOptions { BaseAddress = Origin, ApiKey = "tb_secretvalue" };
        var invitation = new TerraBaseCreatedInvitation(
            new TerraBaseInvitation("i", "a@example.com", TerraBaseRole.Member, "u", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "pending"), "org.inv.secretvalue");

        await Assert.That(options.ToString()).DoesNotContain("secretvalue");
        await Assert.That(invitation.ToString()).DoesNotContain("secretvalue");
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
        return token => database.GetUsageAsync(cancellationToken: token);
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
