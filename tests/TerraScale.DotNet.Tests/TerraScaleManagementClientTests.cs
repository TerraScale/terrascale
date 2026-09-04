using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleManagementClientTests
{
    [Test]
    public async Task SurfaceComposesPublishedClientsAndGapSurfaces()
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var client = Client(http);
        var management = new TerraScaleManagementClient(client);
        await Assert.That(ReferenceEquals(management.Databases, client.Databases)).IsTrue();
        await Assert.That(ReferenceEquals(management.Collections, client.Collections)).IsTrue();
        await Assert.That(ReferenceEquals(management.Backups, client.Backups)).IsTrue();
        await Assert.That(typeof(TerraScaleManagementClient).GetProperty("Branches")).IsNull();
        var usage = await Assert.ThrowsAsync<TerraScaleContractGapException>(() => management.Usage.GetAsync("db"));
        var export = await Assert.ThrowsAsync<TerraScaleContractGapException>(() => management.Exports.CreateAsync("db"));
        await Assert.That(usage!.Operation).IsEqualTo("usage.database.get");
        await Assert.That(export!.Operation).IsEqualTo("exports.create");
        await Assert.That(handler.Request).IsNull();
    }

    [Test]
    public async Task GapOperationsValidateInputsBeforeFailing()
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var management = new TerraScaleManagementClient(Client(http));
        var blank = await Assert.ThrowsAsync<ArgumentException>(() => management.Usage.GetAsync(" "));
        var format = await Assert.ThrowsAsync<ArgumentException>(() => management.Exports.CreateAsync(
            "db", new TerraScaleCreateExportRequest("csv")));
        await Assert.That(blank).IsNotNull();
        await Assert.That(format).IsNotNull();
        await Assert.That(handler.Request).IsNull();
    }

    private static TerraScaleClient Client(HttpClient http) => new(http, new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.example.invalid/"),
        ApiKey = "test-key"
    });

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public RecordedRequest? Request { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = new RecordedRequest(request.Method, request.RequestUri?.ToString());
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string? Uri);
}
