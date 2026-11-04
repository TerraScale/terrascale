using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseManagementParityTests
{
}

public sealed class TerraBaseProjectClosureTests
{
    private const string Project = "01JA0000000000000000000005";
    private const string Organization = "01JA0000000000000000000006";
    private const string Closure = "01JA0000000000000000000007";

    // A success body that answers another closure, or calls a 202 accepted, proves nothing.

    private static Task<TerraBaseProjectClosure> SendClosureAsync(TerraBaseManagementClient client, int version) =>
        version == 1 ? client.CloseProjectAsync(Project, Organization, Closure) : client.EraseProjectAsync(Project, Organization, Closure);
}

public sealed class TerraBaseHistoryErasureTests
{
    private const string Erasure = "01JA0000000000000000000009";

    [Test]
    [Arguments(200, "complete", true)]
    [Arguments(202, "reverify", false)]
    public async Task History_erasure_answers_report_their_status_and_send_no_user(int status, string state, bool complete)
    {
        HttpRequestMessage? sent = null;
        string? body = null;
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            sent = request;
            body = await request.Content!.ReadAsStringAsync(token);
            var response = TerraBaseIntegrationFixture.Json($$"""
                {"status":"{{state}}","erasureId":"{{Erasure}}","pendingHolders":0,"activeDueAt":"2026-11-22T00:00:00Z","backupDueAt":"2026-12-22T00:00:00Z",
                 "activeOverdue":false,"backupOverdue":false,"settlementToken":{{(complete ? "null" : "\"01JA000000000000000000000A\"")}}}
                """);
            response.StatusCode = (System.Net.HttpStatusCode)status;
            return response;
        });
        using var http = new HttpClient(handler);
        var client = new TerraBaseManagementClient(http, new TerraBaseManagementClientOptions { BaseAddress = new Uri("http://100.64.0.1:8081") });

        var answer = await client.RequestHistoryErasureAsync(Erasure, new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero));
        await Assert.That(answer.Complete).IsEqualTo(complete);
        await Assert.That(answer.SettlementToken is null).IsEqualTo(complete);
        await Assert.That(sent!.RequestUri!.AbsolutePath).IsEqualTo($"/admin/history-erasures/{Erasure}");
        await Assert.That(sent.Headers.Contains("X-TerraBase-User-Id")).IsFalse();
        await Assert.That(body).IsEqualTo("""{"version":1,"requestedAt":"2026-10-23T00:00:00+00:00"}""");
    }

    // A body for another erasure, or a status contradicting its code, proves nothing.
}
