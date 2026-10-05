using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

/// <summary>Construction-time validation; these need no server.</summary>
public sealed class TerraBaseClientOptionsTests
{
    private static readonly Uri Origin = new("https://terrabase.example.test:8443/");

    [Test]
    public async Task RecommendedHandlerDisablesRedirectsAndCookies()
    {
        using var handler = TerraBaseHttp.CreateHandler();
        await Assert.That(handler.AllowAutoRedirect).IsFalse();
        await Assert.That(handler.UseCookies).IsFalse();
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
    [Arguments(null)]
    [Arguments("")]
    [Arguments("tb key")]
    [Arguments("tb_key\n")]
    public async Task SecretsMustBePrintableTokens(string? secret)
    {
        using var http = new HttpClient();
        await Assert.That(() => new TerraBaseManagementClient(http, new TerraBaseManagementClientOptions { BaseAddress = Origin, ServiceCredential = secret }))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task RequestTimeoutAndResponseBoundMustBeBounded()
    {
        using var http = new HttpClient();
        await Assert.That(() => new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions { BaseAddress = Origin, ApiKey = "tb_key", RequestTimeout = TimeSpan.Zero }))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions { BaseAddress = Origin, ApiKey = "tb_key", RequestTimeout = TimeSpan.FromHours(1) }))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new TerraBaseManagementClient(http, new TerraBaseManagementClientOptions { BaseAddress = Origin, ServiceCredential = "secret", MaxResponseBytes = 1024 }))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task OptionsAndInvitationsNeverPrintSecrets()
    {
        var documents = new TerraBaseDocumentsClientOptions { BaseAddress = Origin, ApiKey = "tb_secretvalue" };
        var management = new TerraBaseManagementClientOptions { BaseAddress = Origin, ServiceCredential = "dashboard_secretvalue" };
        var invitation = new TerraBaseCreatedInvitation(
            new TerraBaseInvitation("i", "a@example.com", TerraBaseRole.Member, "u", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), "org.inv.secretvalue");

        await Assert.That(documents.ToString()).DoesNotContain("secretvalue");
        await Assert.That(management.ToString()).DoesNotContain("secretvalue");
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
        var management = new TerraBaseManagementClient(http, new TerraBaseManagementClientOptions { EndpointResolver = resolver, ServiceCredential = "secret" });

        await Assert.That(async () => { await management.GetOrganizationAsync("user", organizationId); }).Throws<ArgumentException>();
        await Assert.That(() => management.DeleteOrganizationAsync("user", organizationId)).Throws<ArgumentException>();
        await Assert.That(resolver.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task ResolverOptionsAreExclusiveAndStaticOriginsAreValidated()
    {
        using var http = new HttpClient();
        var resolver = new TerraBaseStaticEndpointResolver(Origin);
        await Assert.That(await resolver.ResolveAsync()).IsEqualTo(Origin);
        await Assert.That(() => new TerraBaseStaticEndpointResolver(new Uri("http://localhost/"))).Throws<ArgumentException>();
        await Assert.That(() => new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions { BaseAddress = Origin, EndpointResolver = resolver, ApiKey = "tb_key" }))
            .Throws<ArgumentException>();
        await Assert.That(() => new TerraBaseManagementClient(http, new TerraBaseManagementClientOptions { BaseAddress = Origin, EndpointResolver = resolver, ServiceCredential = "secret" }))
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
