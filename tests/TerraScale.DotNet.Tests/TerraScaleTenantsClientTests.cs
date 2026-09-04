using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleTenantsClientTests
{
    [Test]
    public async Task UnpublishedOperationsReturnTypedGapWithoutSending()
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var tenants = new TerraScaleTenantsClient(Client(http));
        var create = await Assert.ThrowsAsync<TerraScaleContractGapException>(() => tenants.CreateAsync(
            new TerraScaleCreateTenantRequest("acme", "saas", "br-only")));
        var get = await Assert.ThrowsAsync<TerraScaleContractGapException>(() => tenants.GetAsync("acme"));
        var list = await Assert.ThrowsAsync<TerraScaleContractGapException>(() => tenants.ListAsync());
        var deleted = await Assert.ThrowsAsync<TerraScaleContractGapException>(() => tenants.DeleteAsync(
            "acme", new TerraScaleDeleteTenantOptions(true, "delete-acme")));
        var key = await Assert.ThrowsAsync<TerraScaleContractGapException>(() => tenants.CreateKeyAsync("acme"));
        var branch = await Assert.ThrowsAsync<TerraScaleContractGapException>(() => tenants.CreateBranchAsync("acme"));
        await Assert.That(create!.Operation).IsEqualTo("tenants.create");
        await Assert.That(get!.Code).IsEqualTo("contract_gap");
        await Assert.That(list!.Contract).IsEqualTo("terrabase/openapi.json");
        await Assert.That(deleted!.Operation).IsEqualTo("tenants.delete");
        await Assert.That(key!.Operation).IsEqualTo("tenants.keys.create");
        await Assert.That(branch!.Operation).IsEqualTo("tenants.branches.create");
        await Assert.That(handler.Requests).Count().IsEqualTo(0);
    }

    [Test]
    public async Task CreateValidatesPlacementLockBeforeGap()
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var tenants = new TerraScaleTenantsClient(Client(http));
        var empty = await Assert.ThrowsAsync<ArgumentException>(() => tenants.CreateAsync(
            new TerraScaleCreateTenantRequest("acme", PlacementLock: Array.Empty<string>())));
        var duplicate = await Assert.ThrowsAsync<ArgumentException>(() => tenants.CreateAsync(
            new TerraScaleCreateTenantRequest("acme", PlacementLock: new[] { "GRU", "GRU" })));
        var unknown = await Assert.ThrowsAsync<ArgumentException>(() => tenants.CreateAsync(
            new TerraScaleCreateTenantRequest("acme", PlacementLock: new[] { "SJC" })));
        await Assert.That(empty).IsNotNull();
        await Assert.That(duplicate).IsNotNull();
        await Assert.That(unknown).IsNotNull();
        await Assert.That(handler.Requests).Count().IsEqualTo(0);
    }

    [Test]
    public async Task DeleteRequiresConfirmationAndIdempotency()
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var tenants = new TerraScaleTenantsClient(Client(http));
        var confirmation = await Assert.ThrowsAsync<ArgumentException>(() => tenants.DeleteAsync(
            "acme", new TerraScaleDeleteTenantOptions(false, "delete")));
        var identity = await Assert.ThrowsAsync<ArgumentException>(() => tenants.DeleteAsync(
            "acme", new TerraScaleDeleteTenantOptions(true, " ")));
        await Assert.That(confirmation).IsNotNull();
        await Assert.That(identity).IsNotNull();
        await Assert.That(handler.Requests).Count().IsEqualTo(0);
    }

    private static TerraScaleClient Client(HttpClient http) => new(http, new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.example.invalid/"),
        ApiKey = "test-key"
    });

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
