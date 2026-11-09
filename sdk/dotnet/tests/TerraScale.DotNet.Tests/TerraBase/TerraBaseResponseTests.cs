using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using System.Net;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseResponseTests
{
    private const string RequestId = "01JA0000000000000000000001";

    [Test]
    [Arguments(null, false)]
    [Arguments("", false)]
    [Arguments("oversized", false)]
    [Arguments("01JA0000000000000000000001", false)]
    [Arguments("01JA0000000000000000000001", true)]
    [Arguments("01ja0000000000000000000001", false)]
    [Arguments("81JA0000000000000000000001", false)]
    [Arguments("01JA000000000000000000000I", false)]
    [Arguments("01JA0000000000000000000001,01JA0000000000000000000001", false)]
    [Arguments(" 01JA0000000000000000000001", false)]
    public async Task Request_identity_accepts_only_one_canonical_response_ULID(string? header, bool duplicate)
    {
        using var response = TerraBaseIntegrationFixture.Json("{", HttpStatusCode.BadRequest);
        if (header == "oversized") header = new string('A', 4096);
        if (header is not null) response.Headers.TryAddWithoutValidation("x-request-id", duplicate ? [header, header] : [header]);
        var dataError = await TerraBaseException.FromResponseAsync(response, null, CancellationToken.None);
        var authError = await TerraScaleAuthErrors.CreateAsync(response, CancellationToken.None);
        var expected = header == RequestId && !duplicate ? RequestId : null;
        await Assert.That(dataError.RequestId).IsEqualTo(expected);
        await Assert.That(authError.RequestId).IsEqualTo(expected);
        await Assert.That(dataError.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(authError.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }
}

/// <summary>Response bounds, deadlines and resolution over a fake transport; no TerraBase runs in this repository.</summary>
public sealed class TerraBaseResponseIntegrationTests
{
    private static readonly Uri Origin = new("https://terrabase.example.test/");
    private static readonly Uri Private = new("http://100.64.0.1:8081/");
    private const string DatabasePath = "/v1/projects/01JA0000000000000000000001/databases/bounds";

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SuccessResponseHonorsTheExactByteLimit(bool oneByteOver)
    {
        var body = "{\"id\":\"01JA0000000000000000000009\",\"projectId\":\"01JA0000000000000000000001\",\"name\":\"" + new string('x', 64 * 1024) + "\"}";
        var bytes = System.Text.Encoding.UTF8.GetByteCount(body);
        using var handler = Answer(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        using var http = new HttpClient(handler);
        var connection = new TerraBaseConnection(http, Origin, null, "tb_test_key", "ApiKey", TimeSpan.FromSeconds(30), bytes - (oneByteOver ? 1 : 0));

        var call = () => connection.SendAsync(HttpMethod.Get, DatabasePath, null, TerraBaseJsonContext.Wire.TerraBaseDatabaseInfo, null, CancellationToken.None);
        if (oneByteOver)
        {
            var error = await Assert.ThrowsAsync<TerraBaseException>(call);
            await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
            await Assert.That(error.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(error.Message).Contains("larger than the supported response bound");
        }
        else
        {
            var (result, status) = await call();
            await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(result.Id).IsEqualTo("01JA0000000000000000000009");
            await Assert.That(result.Name.Length).IsEqualTo(64 * 1024);
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task CancellationDuringResponseProcessingCannotReturnSuccess(bool deadlineExpires, bool parsingFails)
    {
        using var handler = Answer(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new TerraBaseDatabaseInfo("01JA0000000000000000000002", "01JA0000000000000000000001", "cancellation"), TerraBaseJsonContext.Wire.TerraBaseDatabaseInfo), Encoding.UTF8, "application/json"),
        });
        using var http = new HttpClient(handler);
        using var canceled = new CancellationTokenSource();
        var clock = new ResponseDeadlineClock();
        var parsed = false;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new ProcessingConverter(() =>
        {
            parsed = true;
            if (deadlineExpires)
            {
                // Fire the deadline inside parsing without relying on ThreadPool timer scheduling.
                clock.Expire();
            }
            else
            {
                canceled.Cancel();
            }

            if (parsingFails)
            {
                throw new JsonException("Response parsing failed after cancellation.");
            }
        }));
        var typeInfo = new TerraBaseJsonContext(options).TerraBaseDatabaseInfo;
        var connection = new TerraBaseConnection(http, Origin, null, "tb_test_key", "ApiKey", TimeSpan.FromSeconds(1), TerraBaseConnection.DefaultMaxResponseBytes, timeProvider: clock);
        var call = () => connection.SendAsync(HttpMethod.Get, "/v1/projects/01JA0000000000000000000001/databases/cancellation", null, typeInfo, null, canceled.Token);
        if (deadlineExpires)
        {
            await Assert.That(call).Throws<TimeoutException>();
        }
        else
        {
            await Assert.That(call).Throws<OperationCanceledException>();
        }

        await Assert.That(parsed).IsTrue();
        await Assert.That(clock.DueTime).IsEqualTo(TimeSpan.FromSeconds(1));
    }

    private sealed class ResponseDeadlineClock : TimeProvider
    {
        private ResponseDeadlineTimer? timer;
        public TimeSpan DueTime { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            return timer = new ResponseDeadlineTimer(callback, state);
        }

        public void Expire() => (timer ?? throw new InvalidOperationException("The request deadline has not been created.")).Fire();

        private sealed class ResponseDeadlineTimer(TimerCallback callback, object? state) : ITimer
        {
            private TimerCallback? pending = callback;
            public void Fire() => Interlocked.Exchange(ref pending, null)?.Invoke(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() => Interlocked.Exchange(ref pending, null);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static TerraBaseTestHandler Answer(Func<HttpResponseMessage> response) => new((_, _) => Task.FromResult(response()));

    private sealed class ProcessingConverter(Action processed) : JsonConverter<TerraBaseDatabaseInfo>
    {
        public override TerraBaseDatabaseInfo Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var database = JsonSerializer.Deserialize(ref reader, TerraBaseJsonContext.Wire.TerraBaseDatabaseInfo)!;
            processed();
            return database;
        }

        public override void Write(Utf8JsonWriter writer, TerraBaseDatabaseInfo value, JsonSerializerOptions options) => throw new NotSupportedException();
    }
}
