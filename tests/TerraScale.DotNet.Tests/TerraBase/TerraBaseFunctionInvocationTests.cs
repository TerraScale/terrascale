using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseFunctionInvocationTests
{
    private const string Root = "/v1/projects/" + TerraBaseIntegrationFixture.ProjectId + "/databases/app/functions/hello";

    [Test]
    [Arguments(200)]
    [Arguments(201)]
    [Arguments(204)]
    [Arguments(302)]
    [Arguments(401)]
    [Arguments(403)]
    [Arguments(404)]
    [Arguments(413)]
    [Arguments(422)]
    [Arguments(500)]
    [Arguments(503)]
    public async Task Invocation_preserves_status_media_and_raw_body_after_disposal(int status)
    {
        byte[] bytes = status == 204 ? [] : [0, 255, 13, 10, 42];
        var calls = 0;
        using var reply = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(bytes) };
        reply.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            calls++;
            await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
            await Assert.That(request.RequestUri!.AbsolutePath).IsEqualTo(Root + "/invoke");
            await Assert.That(request.RequestUri.Query).IsEqualTo("?a%26%3D%3F=%26%2B%3F");
            await Assert.That(request.Headers.Authorization!.ToString()).IsEqualTo("Bearer project-user-token");
            await Assert.That(request.Headers.Contains("Idempotency-Key")).IsFalse();
            await Assert.That(await request.Content!.ReadAsStringAsync(token)).IsEqualTo("raw text");
            return reply;
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        Task<TerraBaseFunctionResponse> Invoke() => db.InvokeFunctionAsync("hello", "raw text"u8.ToArray(), new Dictionary<string, string> { ["a&=?"] = "&+?" });
        var result = status < 300 ? await Invoke() : (await Assert.ThrowsAsync<TerraBaseFunctionInvocationException>(() => Invoke()))!.Response;
        await Assert.That(result.StatusCode).IsEqualTo((HttpStatusCode)status);
        await Assert.That(result.ContentType).IsEqualTo("application/octet-stream");
        await Assert.That(result.Body.ToArray()).IsEquivalentTo(bytes);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reply.Content.ReadAsByteArrayAsync());
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [Arguments(401, "unauthorized")]
    [Arguments(403, "forbidden_scope")]
    [Arguments(500, "function_error")]
    public async Task Json_server_errors_keep_their_code_body_and_request_metadata(int status, string code)
    {
        var raw = "{\"error\":{\"code\":\"" + code + "\",\"message\":\"refused\"}}";
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            var reply = TerraBaseIntegrationFixture.Json(raw, (HttpStatusCode)status);
            reply.Headers.Add("X-Request-ID", TerraBaseIntegrationFixture.ProjectId);
            reply.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
            return Task.FromResult(reply);
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var error = await Assert.ThrowsAsync<TerraBaseFunctionInvocationException>(() => db.InvokeFunctionAsync("hello"));
        await Assert.That(error!.ErrorCode).IsEqualTo(code);
        await Assert.That(error.StatusCode).IsEqualTo((HttpStatusCode)status);
        await Assert.That(Encoding.UTF8.GetString(error.Response.Body.Span)).IsEqualTo(raw);
        await Assert.That(error.RequestId).IsEqualTo(TerraBaseIntegrationFixture.ProjectId);
        await Assert.That(error.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task Input_limit_and_log_limits_fail_before_transport_and_one_MiB_is_allowed()
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            calls++;
            await Assert.That((await request.Content!.ReadAsByteArrayAsync(token)).Length).IsEqualTo(1024 * 1024);
            return TerraBaseIntegrationFixture.Json("null");
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => db.InvokeFunctionAsync("hello", new byte[1024 * 1024 + 1]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => db.ListFunctionLogsAsync("hello", 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => db.ListFunctionLogsAsync("hello", 101));
        await Assert.That(calls).IsEqualTo(0);
        await db.InvokeFunctionAsync("hello", new byte[1024 * 1024]);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task Observations_preserve_log_order_wide_numbers_and_nullable_delivery_state()
    {
        using var manifest = JsonDocument.Parse(TerraBaseIntegrationFixture.ReadContract("function-invocations.json"));
        var calls = 0;
        using var handler = new TerraBaseTestHandler(async (request, _) =>
        {
            var route = manifest.RootElement.GetProperty("observations")[calls++];
            await Assert.That(request.Method).IsEqualTo(HttpMethod.Get);
            await Assert.That(request.RequestUri!.PathAndQuery).IsEqualTo(Root + route.GetProperty("suffix").GetString());
            await Assert.That(route.GetProperty("scopes").GetArrayLength()).IsEqualTo(2);
            return TerraBaseIntegrationFixture.Json(route.GetProperty("fixture").GetRawText());
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var logs = await db.ListFunctionLogsAsync("hello", 2);
        await Assert.That(logs[0].DurationMs).IsEqualTo(2147483648L);
        await Assert.That(logs[0].Error).IsNull();
        await Assert.That(logs[1].Error).IsEqualTo("failed");
        var deliveries = await db.ListFunctionDeliveriesAsync("hello");
        await Assert.That(deliveries[0].Attempts).IsEqualTo(2147483648L);
        await Assert.That(deliveries[0].HttpStatus).IsNull();
        await Assert.That(deliveries[0].NextAttemptAt).IsNull();
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task Cancellation_and_deadlines_cover_invocation_response_reads_without_replay()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new WaitingBody(started)) });
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => db.InvokeFunctionAsync("hello", cancellationToken: canceled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => db.ListFunctionLogsAsync("hello", cancellationToken: canceled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => db.ListFunctionDeliveriesAsync("hello", canceled.Token));
        await Assert.That(calls).IsEqualTo(0);
        using var cancel = new CancellationTokenSource();
        var pending = db.InvokeFunctionAsync("hello", cancellationToken: cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancel.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        var timed = TerraBaseIntegrationFixture.Client(http, TimeSpan.FromMilliseconds(30)).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        await Assert.ThrowsAsync<TimeoutException>(() => timed.InvokeFunctionAsync("hello"));
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task Response_bounds_apply_to_raw_errors_and_caller_HttpClient_stays_owned()
    {
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new ByteArrayContent(new byte[65537]) }));
        using var http = new HttpClient(handler);
        var client = new TerraBaseDocumentsClient(http, new() { Region = "sjc", ApiKey = "project-token", MaxResponseBytes = 65536 });
        var db = client.Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => db.InvokeFunctionAsync("hello"));
        await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
        using var stillUsable = await http.GetAsync("https://sjc.api.terrascale.tech" + Root + "/logs");
        await Assert.That(stillUsable.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    private sealed class WaitingBody(TaskCompletionSource started) : MemoryStream
    {
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken token)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
    }
}
