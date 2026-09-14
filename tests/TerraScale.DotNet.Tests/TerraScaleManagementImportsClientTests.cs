using System.Net;
using System.Net.Http.Headers;
using System.Text;
using TerraScale.DotNet;
using TerraScale.DotNet.Generated;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleManagementImportsClientTests
{
    private const string ImportJobJson = "{\"committed_records\":0,\"cursor\":0,\"database_id\":\"db-1\",\"durability\":\"durable\",\"error\":null,\"format\":\"jsonl\",\"input_committed\":true,\"input_digest\":null,\"input_length\":3,\"operation_id\":\"import-op-1\",\"pending_cursor\":null,\"pending_operation\":null,\"pending_records\":null,\"project_id\":\"project-1\",\"state\":\"completed\",\"tenant_id\":\"tenant-1\"}";

    [Test]
    public async Task CreateAsync_UsesEscapedRouteAndTrimmedRequestValues()
    {
        using var handler = new RecordingHandler(HttpStatusCode.Accepted, ImportJobJson);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var job = await client.Management.Imports.CreateAsync(" db/one ", " jsonl ", " import-key ");

        await Assert.That(handler.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(handler.Uri).IsEqualTo("https://api.example.invalid/api/v1/databases/db%2Fone/imports");
        await Assert.That(handler.ContentType).IsEqualTo("application/json");
        await Assert.That(handler.Body).IsEqualTo("{\"format\":\"jsonl\",\"idempotency_key\":\"import-key\"}");
        await Assert.That(handler.IdempotencyKey).IsEqualTo("import-key");
        await Assert.That(job.OperationId).IsEqualTo("import-op-1");
    }

    [Test]
    public async Task CreateAsync_RejectsUnknownContractFormatBeforeSending()
    {
        using var handler = new RecordingHandler(HttpStatusCode.Accepted, ImportJobJson);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        await Assert.ThrowsAsync<ArgumentException>(() => client.Management.Imports.CreateAsync("db", "unsupported", "import-key"));
        await Assert.That(handler.Method).IsNull();
    }

    [Test]
    public async Task UploadAsync_UsesBinaryInputRouteAndPreservesPayload()
    {
        using var handler = new RecordingHandler(HttpStatusCode.Accepted, ImportJobJson);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);
        byte[] payload = [0, 1, 2, 255];

        var job = await client.Management.Imports.UploadAsync("db/one", "job/one", payload);

        await Assert.That(handler.Method).IsEqualTo(HttpMethod.Put);
        await Assert.That(handler.Uri).IsEqualTo("https://api.example.invalid/api/v1/databases/db%2Fone/imports/job%2Fone/input");
        await Assert.That(handler.ContentType).IsEqualTo("application/octet-stream");
        await Assert.That(handler.Bytes).IsEquivalentTo(payload);
        await Assert.That(handler.IdempotencyKey).IsNull();
        await Assert.That(job.InputLength).IsEqualTo(3);
    }

    [Test]
    public async Task ResumeAsync_UsesResumeRouteWithoutRequestBody()
    {
        using var handler = new RecordingHandler(HttpStatusCode.Accepted, ImportJobJson);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var job = await client.Management.Imports.ResumeAsync("db/one", "job/one");

        await Assert.That(handler.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(handler.Uri).IsEqualTo("https://api.example.invalid/api/v1/databases/db%2Fone/imports/job%2Fone/resume");
        await Assert.That(handler.Body).IsNull();
        await Assert.That(handler.ContentType).IsNull();
        await Assert.That(handler.IdempotencyKey).IsNull();
        await Assert.That(job.State).IsEqualTo("completed");
    }

    [Test]
    public async Task ListAndGetAsync_UseImportRoutesAndGeneratedCollectionMetadata()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "[" + ImportJobJson + "]");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var jobs = await client.Management.Imports.ListAsync("db/one");

        await Assert.That(handler.Method).IsEqualTo(HttpMethod.Get);
        await Assert.That(handler.Uri).IsEqualTo("https://api.example.invalid/api/v1/databases/db%2Fone/imports");
        await Assert.That(jobs).Count().IsEqualTo(1);
        await Assert.That(jobs[0].OperationId).IsEqualTo("import-op-1");

        handler.ResponseBody = ImportJobJson;
        var job = await client.Management.Imports.GetAsync("db/one", "job/one");

        await Assert.That(handler.Uri).IsEqualTo("https://api.example.invalid/api/v1/databases/db%2Fone/imports/job%2Fone");
        await Assert.That(job.OperationId).IsEqualTo("import-op-1");
        await Assert.That(TerraScaleContractJsonContext.Default.IReadOnlyListImportJob).IsNotNull();
    }

    [Test]
    public async Task UploadAsync_StreamReadsPayloadAndUsesTheSameBinaryProducer()
    {
        using var handler = new RecordingHandler(HttpStatusCode.Accepted, ImportJobJson);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);
        await using var content = new MemoryStream([4, 5, 6]);

        var job = await client.Management.Imports.UploadAsync("db/one", "job/one", content);

        await Assert.That(handler.Method).IsEqualTo(HttpMethod.Put);
        await Assert.That(handler.Bytes).IsEquivalentTo(new byte[] { 4, 5, 6 });
        await Assert.That(job.OperationId).IsEqualTo("import-op-1");
    }

    private static TerraScaleClient CreateClient(HttpClient httpClient) => new(httpClient, new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.example.invalid/"),
        ApiKey = "test-key"
    });

    private sealed class RecordingHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        public string ResponseBody { get; set; } = responseBody;
        public HttpMethod? Method { get; private set; }
        public string? Uri { get; private set; }
        public string? Body { get; private set; }
        public byte[]? Bytes { get; private set; }
        public string? ContentType { get; private set; }
        public string? IdempotencyKey { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri?.ToString();
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            if (request.Content is not null)
            {
                Bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                Body = Encoding.UTF8.GetString(Bytes);
            }
            IdempotencyKey = request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null;
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
