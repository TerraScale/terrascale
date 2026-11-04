using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAuthStepUpContractTests
{
    private const string Proof = "fixture-proof-canary";
    private const string Grant = "fixture-operation-grant-canary";
    private const string Receipt = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Id = "01JA0000000000000000000005";

    [Test]
    [Arguments("request_step_up")]
    [Arguments("consume_step_up")]
    [Arguments("consume_step_up_delete")]
    public async Task StepUpWireContractMatchesSnapshot(string operation)
    {
        using var fixtures = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "contracts/auth/fixtures/platform-headless.json")));
        var fixture = fixtures.RootElement.GetProperty(operation);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "contracts/auth/routes.json")));
        var contract = manifest.RootElement.GetProperty("operations").EnumerateArray().Single(item =>
            item.GetProperty("operation_id").GetString() == (operation == "consume_step_up_delete" ? "consume_step_up" : operation));
        var status = (HttpStatusCode)contract.GetProperty(operation == "consume_step_up_delete" ? "deletion_success_status" : "success_status").GetInt32();
        using var handler = new CaptureHandler(status, fixture.GetProperty("response").GetRawText());
        using var http = new HttpClient(handler);
        var client = Client(http);
        var request = fixture.GetProperty("request").GetRawText();
        if (operation == "request_step_up")
        {
            var result = await client.Platform.BeginStepUpAsync("fixture-sign-in", JsonSerializer.Deserialize(request,
                TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthStepUpRequest)!);
            await Assert.That(result.Proof).IsEqualTo(Proof);
            await Assert.That(result.StepUpUrl).IsEqualTo("https://auth.example.test/ui/terrascale/step-up/" + Id);
            await Assert.That(result.ExpiresAt).IsEqualTo(DateTimeOffset.Parse("2026-10-26T12:05:00Z", System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            var grant = await client.Platform.ConsumeStepUpAsync("fixture-sign-in", JsonSerializer.Deserialize(request,
                TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthStepUpConsume)!);
            await Assert.That(grant).IsEqualTo(operation == "consume_step_up_delete" ? Grant : null);
        }
        await Assert.That(handler.Method).IsEqualTo(contract.GetProperty("method").GetString());
        await Assert.That(handler.Path).IsEqualTo(contract.GetProperty("path").GetString());
        await Assert.That(handler.Authorization).IsEqualTo("Bearer fixture-sign-in");
        await Assert.That(handler.MediaType).IsEqualTo("application/json");
        await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(handler.Body!), JsonNode.Parse(request))).IsTrue();
        await Assert.That(handler.StepUpProof).IsNull();
        await Assert.That(handler.Calls).IsEqualTo(1);
        await Assert.That(http.DefaultRequestHeaders.Contains("X-TerraScale-Step-Up-Proof")).IsFalse();
    }

    [Test]
    [Arguments("begin")]
    [Arguments("consume")]
    [Arguments("consume_delete")]
    [Arguments("delete")]
    [Arguments("create_org_key")]
    [Arguments("rotate_org_key")]
    [Arguments("create_project_key")]
    [Arguments("rotate_project_key")]
    [Arguments("create_client")]
    public async Task ErrorsAndLostResponsesNeverRepeatSensitiveOperations(string operation)
    {
        foreach (var status in new[] { 200, 400, 401, 403, 409, 429, 503, 0 })
        {
            using var handler = new CaptureHandler((HttpStatusCode)status, """{"error":{"code":"step_up_required","message":"fixture-proof-canary"}}""");
            using var http = new HttpClient(handler);
            if (status == 0) await Assert.ThrowsAsync<HttpRequestException>(() => Call(Client(http), operation));
            else
            {
                var error = await Assert.ThrowsAsync<TerraScaleAuthException>(() => Call(Client(http), operation));
                await Assert.That(error!.ErrorCode).IsEqualTo(status == 200 ? "invalid_response" : "step_up_required");
                await Assert.That(error.ToString()).DoesNotContain(Proof);
            }
            await Assert.That(handler.Calls).IsEqualTo(1);
        }
    }

    private static Task Call(TerraScaleAuthClient client, string operation, string proof = Proof, CancellationToken cancellationToken = default) => operation switch
    {
        "begin" => client.Platform.BeginStepUpAsync("fixture-sign-in", new("create_pat", "ts-dashboard", "https://dashboard.example.test/auth/step-up/callback"), cancellationToken),
        "consume" => client.Platform.ConsumeStepUpAsync("fixture-sign-in", new(proof, "create_pat", "ts-dashboard"), cancellationToken),
        "consume_delete" => client.Platform.ConsumeStepUpAsync("fixture-sign-in", new(proof, "delete_account", "ts-dashboard", Receipt), cancellationToken),
        "delete" => client.Platform.DeleteAccountWithProofAsync("fixture-sign-in", proof, cancellationToken: cancellationToken),
        "create_org_key" => client.Platform.CreateOrganizationApiKeyAsync("fixture-sign-in", Id, new("Synthetic", ["sql:read"]), proof, cancellationToken: cancellationToken),
        "rotate_org_key" => client.Platform.RotateOrganizationApiKeyAsync("fixture-sign-in", Id, Id, proof, cancellationToken: cancellationToken),
        "create_project_key" => client.Platform.CreateProjectApiKeyAsync("fixture-sign-in", Id, Id, new("Synthetic", ["sql:read"]), proof, cancellationToken: cancellationToken),
        "rotate_project_key" => client.Platform.RotateProjectApiKeyAsync("fixture-sign-in", Id, Id, Id, proof, cancellationToken: cancellationToken),
        "create_client" => client.Projects.CreateClientAsync("fixture-sign-in", Id, new("Synthetic", "confidential", ["https://example.test/cb"]), proof, cancellationToken: cancellationToken),
        _ => throw new ArgumentException("Unknown operation.", nameof(operation))
    };

    private static TerraScaleAuthClient Client(HttpClient http) => new(http, new TerraScaleAuthClientOptions
    {
        BaseAddress = new Uri("https://auth.example.test"),
        InstanceSlug = "terrascale",
    });

    private sealed class ConsumeRecoveryHandler(bool responseLost) : HttpMessageHandler
    {
        public List<(string Path, string? Body, string? Receipt, string? Grant, string? Proof)> Requests { get; } = [];
        private readonly HashSet<string> reservedReceipts = new(StringComparer.Ordinal);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((path, body,
                request.Headers.TryGetValues("X-TerraScale-Deletion-Receipt", out var receipts) ? receipts.Single() : null,
                request.Headers.TryGetValues("X-TerraScale-Deletion-Authorization", out var grants) ? grants.Single() : null,
                request.Headers.TryGetValues("X-TerraScale-Step-Up-Proof", out var proofs) ? proofs.Single() : null));
            if (path == "/v1/terrascale/step-up")
                return Response(HttpStatusCode.Created, """{"proof":"fixture-fresh-proof","step_up_url":"https://auth.example.test/step-up/fresh","expires_at":"2026-10-26T12:05:00Z"}""");
            if (path == "/v1/terrascale/account") return Response(HttpStatusCode.Accepted, "");
            using var consume = JsonDocument.Parse(body!);
            var receipt = consume.RootElement.GetProperty("operation_receipt").GetString()!;
            // Model the late transaction having reserved its receipt hash before acknowledgement is lost/refused.
            if (!reservedReceipts.Add(receipt)) return Response(HttpStatusCode.Forbidden, """{"error":{"code":"step_up_required"}}""");
            if (Requests.Count == 1)
            {
                if (responseLost) throw new HttpRequestException("Synthetic lost consume acknowledgement.");
                return Response(HttpStatusCode.Forbidden, """{"error":{"code":"step_up_required"}}""");
            }
            return Response(HttpStatusCode.OK, """{"operation_grant":"fixture-operation-grant-canary"}""");
        }
        private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class CancellationHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class ConcurrentHandler : HttpMessageHandler
    {
        public ConcurrentDictionary<string, (string Proof, string Receipt)> Headers { get; } = new();
        public TaskCompletionSource BothEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Headers[request.Headers.Authorization!.Parameter!] = (request.Headers.GetValues("X-TerraScale-Step-Up-Proof").Single(),
                request.Headers.GetValues("X-TerraScale-Deletion-Receipt").Single());
            if (Interlocked.Increment(ref calls) == 2) BothEntered.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private sealed class CaptureHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Method { get; private set; }
        public string? Path { get; private set; }
        public string? Body { get; private set; }
        public string? MediaType { get; private set; }
        public string? Authorization { get; private set; }
        public string? StepUpProof { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Method = request.Method.Method;
            Path = request.RequestUri!.PathAndQuery;
            Authorization = request.Headers.Authorization?.ToString();
            StepUpProof = request.Headers.TryGetValues("X-TerraScale-Step-Up-Proof", out var proofs) ? proofs.Single() : null;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            MediaType = request.Content?.Headers.ContentType?.MediaType;
            // The request has reached the peer; its response is lost before the caller can learn the outcome.
            if ((int)status == 0) throw new HttpRequestException("Synthetic lost response.");
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
        }
    }
}
