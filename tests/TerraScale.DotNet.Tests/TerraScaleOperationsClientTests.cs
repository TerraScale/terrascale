using System.Net;
using System.Text;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleOperationsClientTests
{
    [Test]
    public async Task ListAsync_UsesProjectScopeAndDecodesPage()
    {
        using var handler = new RecordingHandler("{\"operations\":[{\"operation_id\":\"op-1\",\"kind\":\"database_create\",\"database_id\":\"db-1\",\"name\":\"orders\",\"state\":\"running\",\"stage\":\"members_admitted\",\"created_at\":\"2026-09-06T12:00:00Z\",\"revision\":18446744073709551615,\"cancellable\":false,\"cancellation_reason\":\"\"}],\"next_cursor\":\"cursor/one\",\"covered_kinds\":[\"database_create\",\"branch_create\"]}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var page = await client.Operations.ListAsync(1, "cursor/one");

        await Assert.That(handler.Request).IsEqualTo("https://api.example.invalid/api/v1/operations?after=cursor%2Fone&limit=1");
        await Assert.That(handler.Tenant).IsEqualTo("tenant-1");
        await Assert.That(handler.Project).IsEqualTo("project-1");
        await Assert.That(page.Operations[0].Revision).IsEqualTo(ulong.MaxValue);
        await Assert.That(page.NextCursor).IsEqualTo("cursor/one");
        await Assert.That(page.CoveredKinds).Contains("branch_create");
    }

    [Test]
    public async Task ListAsync_ValidatesProducerBounds()
    {
        using var handler = new RecordingHandler("{}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Operations.ListAsync(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Operations.ListAsync(51));
        await Assert.That(handler.Request).IsNull();
    }

    [Test]
    public async Task ListAsync_PreservesAbsentOptionalJournalFacts()
    {
        using var handler = new RecordingHandler("{\"operations\":[{\"operation_id\":\"op-1\",\"kind\":\"branch_create\",\"database_id\":\"db-1\",\"name\":\"copy\",\"state\":\"queued\",\"stage\":\"accepted\",\"cancellable\":false,\"cancellation_reason\":\"\"}],\"covered_kinds\":[\"database_create\",\"branch_create\"]}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var page = await client.Operations.ListAsync();

        await Assert.That(page.Operations[0].CreatedAt).IsNull();
        await Assert.That(page.Operations[0].Revision).IsNull();
    }

    private static TerraScaleClient CreateClient(HttpClient httpClient) => new(httpClient, new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.example.invalid/"),
        ApiKey = "test-key",
        TenantId = "tenant-1",
        ProjectId = "project-1"
    });

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public string? Request { get; private set; }
        public string? Tenant { get; private set; }
        public string? Project { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request.RequestUri?.ToString();
            Tenant = request.Headers.GetValues("X-TerraScale-Tenant-Id").SingleOrDefault();
            Project = request.Headers.GetValues("X-TerraScale-Project-Id").SingleOrDefault();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
