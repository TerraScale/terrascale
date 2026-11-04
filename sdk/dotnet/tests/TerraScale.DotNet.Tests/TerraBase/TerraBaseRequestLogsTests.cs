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
                .Replace("\"isQuery\":true}", "\"isQuery\":false,\"requestId\":\"customer-correlation\",\"statements\":3,\"statementErrors\":1}", StringComparison.Ordinal))));
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
        await Assert.That((logs.Items[0].Statements, logs.Items[0].StatementErrors)).IsEqualTo((0L, 0L));
        await Assert.That((logs.Items[1].Statements, logs.Items[1].StatementErrors)).IsEqualTo((3L, 1L));
    }

}
