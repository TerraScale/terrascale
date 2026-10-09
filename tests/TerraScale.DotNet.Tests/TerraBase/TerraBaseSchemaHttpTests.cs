using System.Net;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

/// <summary>Records schema requests sent to the real pinned listener; never fabricates HTTP responses.</summary>
[ClassDataSource<TerraBaseServer>(Shared = SharedType.PerTestSession)]
public sealed class TerraBaseSchemaHttpTests(TerraBaseServer server)
{
    [Test]
    public async Task SchemaOperationsSendTheProducerMethodsPathsAndJsonOverHttps()
    {
        server.Require();
        var tenant = await server.CreateTenantAsync();
        using var transport = TerraBaseHttp.CreateHandler();
        var trusted = server.Running.Certificate.RawData;
        transport.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, _) =>
            presented is not null && presented.GetRawCertData().AsSpan().SequenceEqual(trusted);
        using var recorder = new RequestRecorder(transport);
        using var http = new HttpClient(recorder);
        var database = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions
        {
            BaseAddress = server.Running.ClientAddress,
            ApiKey = tenant.ApiKey,
        }).Database(tenant.Project.Id, "app");

        // ff2560 predates these routes. Its real 404 checks transport wiring, not schema10 qualification.
        var declaration = new TerraBaseUniqueSchemaRequest(Indexes:
            [new("instance_email", ["instanceId", "email"], Unique: true, Normalization: "trim_lowercase")]);
        var put = await Assert.ThrowsAsync<TerraBaseNotFoundException>(() => database.DeclareSchemaAsync("people", declaration));
        await Assert.That(put!.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var sent = recorder.Last!;
        await Assert.That(sent.Method).IsEqualTo(HttpMethod.Put);
        await Assert.That(sent.Uri.AbsoluteUri).IsEqualTo(
            $"{server.Running.ClientAddress}v1/projects/{tenant.Project.Id}/databases/app/schema/people");
        await Assert.That(sent.MediaType).IsEqualTo("application/json");
        using (var body = JsonDocument.Parse(sent.Body!))
        {
            var index = body.RootElement.GetProperty("indexes")[0];
            await Assert.That(index.GetProperty("name").GetString()).IsEqualTo("instance_email");
            await Assert.That(index.GetProperty("fields").GetRawText()).IsEqualTo("[\"instanceId\",\"email\"]");
            await Assert.That(index.GetProperty("unique").GetBoolean()).IsTrue();
            await Assert.That(index.GetProperty("normalization").GetString()).IsEqualTo("trim_lowercase");
        }

        var get = await Assert.ThrowsAsync<TerraBaseNotFoundException>(() => database.GetSchemaAsync("people"));
        await Assert.That(get!.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var read = recorder.Last!;
        await Assert.That(read.Method).IsEqualTo(HttpMethod.Get);
        await Assert.That(read.Uri).IsEqualTo(sent.Uri);
        await Assert.That(read.Body).IsNull();
        await Assert.That(recorder.Calls).IsEqualTo(2);
    }

    private sealed record SentRequest(HttpMethod Method, Uri Uri, string? MediaType, string? Body);

    private sealed class RequestRecorder(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public SentRequest? Last { get; private set; }

        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = new(request.Method, request.RequestUri!, request.Content?.Headers.ContentType?.MediaType,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            Calls++;
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
