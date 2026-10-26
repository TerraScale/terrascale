using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E.Features.Databases;

public sealed class DatabaseLimitMessageTests
{
    [Test]
    [Arguments(413, "document exceeds 64 KiB", "The payload is too large. Reduce the size and try again.")]
    [Arguments(429, "stream slots exhausted", "Too many requests on this database. Wait a moment and try again.")]
    public async Task SdkLimitRefusalsKeepTheStatusAndActionableReason(int status, string reason, string message)
    {
        using var wire = new Refusal(status, reason);
        using var http = new HttpClient(wire);
        var sdk = new TerraBaseDocumentsClient(http, new()
        {
            BaseAddress = new("https://gru.api.terrascale.tech"), Region = "gru",
            AccessTokenProvider = _ => ValueTask.FromResult("synthetic-test-token")
        });
        TerraBaseException? failure = null;
        try { await sdk.Project("01JA0000000000000000000001").ListDatabasesAsync(); }
        catch (TerraBaseException error) { failure = error; }
        await Assert.That(failure).IsNotNull();
        await Assert.That(DatabaseResults.KnownFailure(failure!)).IsTrue();
        var problem = ((ProblemHttpResult)DatabaseResults.Failure(failure!)).ProblemDetails;
        await Assert.That(problem.Status).IsEqualTo(status);
        await Assert.That(problem.Detail).IsEqualTo(message + " " + reason);
    }

    private sealed class Refusal(int status, string reason) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { error = new { code = "limit", message = reason } }), Encoding.UTF8, "application/json")
            });
    }
}
