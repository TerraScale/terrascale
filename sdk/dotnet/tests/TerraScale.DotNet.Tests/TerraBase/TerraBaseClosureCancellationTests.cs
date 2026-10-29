using System.Net;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseClosureCancellationTests
{
    private const string Generation = "01JA0000000000000000000007";

    [Test]
    [Arguments(200, "cancelled", Generation, 0, true)]
    [Arguments(200, "committed", Generation, 0, true)]
    [Arguments(202, "pending", Generation, 2, true)]
    [Arguments(200, "pending", Generation, 1, false)]
    [Arguments(202, "cancelled", Generation, 0, false)]
    [Arguments(200, "cancelled", "01JA0000000000000000000008", 0, false)]
    [Arguments(200, "cancelled", Generation, 1, false)]
    public async Task Only_a_matching_all_holder_answer_proves_cancellation(int code, string state, string generation, int pending, bool valid)
    {
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            await Assert.That(request.RequestUri!.AbsolutePath).IsEqualTo($"/admin/projects/{Generation}/databases/auth/closure-cancellations/{Generation}");
            await Assert.That(await request.Content!.ReadAsStringAsync(token)).IsEqualTo($$"""{"version":1,"targetId":"{{Generation}}"}""");
            var response = TerraBaseIntegrationFixture.Json($$"""{"generation":"{{generation}}","status":"{{state}}","pendingHolders":{{pending}}}""");
            response.StatusCode = (HttpStatusCode)code;
            return response;
        });
        using var http = new HttpClient(handler);
        var database = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions
        {
            BaseAddress = new Uri("http://100.64.0.1:8081"),
        }).Database(Generation, "auth");
        if (valid)
        {
            await Assert.That((await database.CancelClosureAsync(Generation, Generation)).Status).IsEqualTo(state);
        }
        else
        {
            var error = await Assert.ThrowsAsync<TerraBaseException>(() => database.CancelClosureAsync(Generation, Generation));
            await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
        }
    }
}
