using System.Net;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseRequestLogsTests
{
    private const string DatabaseId = "01JA0000000000000000000002";
    private const string Entry = """{"id":"01JA0000000000000000000003","time":"2026-10-26T10:00:00Z","region":"gru","method":"POST","route":"/v1/projects/{project}/databases/{database}/collections/{collection}/query","status":200,"latencyMs":1.25,"operations":9007199254740993,"isQuery":true}""";
    private const string Empty = """{"databaseId":"01JA0000000000000000000002","items":[],"truncated":false,"recordingLimited":false}""";
    private static string Body(string items) => "{\"databaseId\":\"" + DatabaseId + "\",\"items\":[" + items + "],\"truncated\":true,\"recordingLimited\":true}";

    [Test]
    public async Task Public_route_filter_and_observation_fields_preserve_the_contract()
    {
        var queries = new List<string>();
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            if (request.Method != HttpMethod.Get || request.Content is not null
                || request.RequestUri!.AbsolutePath != $"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/databases/app/logs")
                throw new InvalidOperationException("Unexpected request-log operation.");
            queries.Add(request.RequestUri.Query);
            return Task.FromResult(TerraBaseIntegrationFixture.Json(Body(Entry + "," + Entry.Replace("0003\"", "0004\"", StringComparison.Ordinal)
                .Replace("\"isQuery\":true}", "\"isQuery\":false,\"requestId\":\"customer-correlation\"}", StringComparison.Ordinal))));
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var logs = await db.ListRequestLogsAsync();
        await db.ListRequestLogsAsync("2xx");
        await db.ListRequestLogsAsync("4xx");
        await Assert.That(queries).IsEquivalentTo(["", "?status=2xx", "?status=4xx"]);
        await Assert.That(logs.DatabaseId).IsEqualTo(DatabaseId);
        await Assert.That(logs.Truncated && logs.RecordingLimited).IsTrue();
        await Assert.That(logs.Items[0].Operations).IsEqualTo(9007199254740993L);
        await Assert.That(logs.Items[0].LatencyMs).IsEqualTo(1.25);
        await Assert.That(logs.Items[0].Time).IsEqualTo(new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.Zero));
        await Assert.That(logs.Items[0].IsQuery).IsTrue();
        await Assert.That(logs.Items[0].RequestId).IsNull();
        await Assert.That(logs.Items[1].RequestId).IsEqualTo("customer-correlation");
    }

    [Test]
    public async Task Missing_null_or_invalid_required_fields_are_not_returned_as_defaults()
    {
        var invalid = new[]
        {
            Empty.Replace(",\"truncated\":false", "", StringComparison.Ordinal),
            Empty.Replace(",\"recordingLimited\":false", "", StringComparison.Ordinal),
            Empty.Replace("\"databaseId\":\"" + DatabaseId + "\",", "", StringComparison.Ordinal),
            Empty.Replace("\"items\":[]", "\"items\":null", StringComparison.Ordinal),
            Empty.Replace("\"items\":[]", "\"items\":[null]", StringComparison.Ordinal),
            Empty.Replace(DatabaseId, "not-an-id", StringComparison.Ordinal),
            Body(Entry.Replace(",\"isQuery\":true", "", StringComparison.Ordinal)),
            Body(Entry.Replace("\"region\":\"gru\"", "\"region\":null", StringComparison.Ordinal)),
            Body(Entry.Replace("\"id\":\"01JA0000000000000000000003\"", "\"id\":null", StringComparison.Ordinal)),
            Body(Entry.Replace("\"status\":200", "\"status\":null", StringComparison.Ordinal)),
            Body(Entry.Replace("\"status\":200", "\"status\":\"200\"", StringComparison.Ordinal)),
            Body(Entry.Replace("\"latencyMs\":1.25", "\"latencyMs\":-1", StringComparison.Ordinal)),
            Body(Entry.Replace("\"operations\":9007199254740993", "\"operations\":-1", StringComparison.Ordinal)),
        };
        foreach (var body in invalid)
        {
            using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(TerraBaseIntegrationFixture.Json(body)));
            using var http = new HttpClient(handler);
            var failure = await Assert.ThrowsAsync<TerraBaseException>(() => TerraBaseIntegrationFixture.Client(http)
                .Database(TerraBaseIntegrationFixture.ProjectId, "app").ListRequestLogsAsync());
            await Assert.That(failure!.ErrorCode).IsEqualTo("invalid_response");
        }
    }

    [Test]
    [Arguments(201, "application/json")]
    [Arguments(200, "text/plain")]
    public async Task Only_the_documented_success_status_and_media_are_accepted(int status, string media)
    {
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(Empty, System.Text.Encoding.UTF8, media),
        }));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => TerraBaseIntegrationFixture.Client(http)
            .Database(TerraBaseIntegrationFixture.ProjectId, "app").ListRequestLogsAsync());
        await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
    }

    [Test]
    [Arguments("200")]
    [Arguments("2XX")]
    [Arguments("2xx&limit=1000")]
    public async Task Invalid_filters_are_refused_before_dispatch(string filter)
    {
        using var handler = new TerraBaseTestHandler((_, _) => throw new InvalidOperationException("Must not send."));
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        await Assert.ThrowsAsync<ArgumentException>(() => db.ListRequestLogsAsync(filter));
    }

    [Test]
    [Arguments(400, "invalid_status")]
    [Arguments(403, "insufficient_scope")]
    [Arguments(404, "not_found")]
    [Arguments(503, "storage_unavailable")]
    public async Task Producer_refusals_preserve_the_error_without_replay(int status, string code)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(TerraBaseIntegrationFixture.Json($$$"""{"error":{"code":"{{{code}}}","message":"refused"}}""", (HttpStatusCode)status));
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => TerraBaseIntegrationFixture.Client(http)
            .Database(TerraBaseIntegrationFixture.ProjectId, "app").ListRequestLogsAsync("4xx"));
        await Assert.That(error!.StatusCode).IsEqualTo((HttpStatusCode)status);
        await Assert.That(error.ErrorCode).IsEqualTo(code);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task Cancellation_stops_reading_and_leaves_the_caller_client_usable()
    {
        var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new TerraBaseTestHandler(async (_, token) =>
        {
            if (++calls != 1) return TerraBaseIntegrationFixture.Json(Empty);
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Cancellation did not interrupt.");
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => db.ListRequestLogsAsync(cancellationToken: cancelled.Token));
        await Assert.That(calls).IsEqualTo(0);
        using var inflight = new CancellationTokenSource();
        var pending = db.ListRequestLogsAsync(cancellationToken: inflight.Token);
        try
        {
            var first = await Task.WhenAny(entered.Task, pending).WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(ReferenceEquals(first, entered.Task)).IsTrue();
            inflight.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            await Assert.That((await db.ListRequestLogsAsync()).Items).IsEmpty();
            await Assert.That(calls).IsEqualTo(2);
        }
        finally
        {
            inflight.Cancel();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
    }
}
