using System.Text;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseUnicodeRequestTests
{
    [Test]
    [Arguments('\u2028')]
    [Arguments('\u3000')]
    [Arguments('\u007f')]
    [Arguments('\u0080')]
    [Arguments('界')]
    public async Task JsonRequestBodiesPreserveCompactUnicode(char character)
    {
        var text = new string(character, 200000);
        var calls = 0;
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            calls++;
            var bytes = await request.Content!.ReadAsByteArrayAsync(token);
            using var sent = JsonDocument.Parse(bytes);
            await Assert.That(sent.RootElement.GetProperty("source").GetString()).IsEqualTo(text);
            // Default producer-style escaping must not become the request's size.
            await Assert.That(bytes.Length <= Encoding.UTF8.GetByteCount(text) + 200).IsTrue();
            await Assert.That(request.Headers.Authorization!.ToString()).IsEqualTo("Bearer project-user-token");
            await Assert.That(request.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");
            return TerraBaseIntegrationFixture.Json("""{"name":"hello","createdAt":"2026-10-26T00:00:00Z","updatedAt":"2026-10-26T00:00:00Z"}""");
        });
        using var http = new HttpClient(handler);
        var client = TerraBaseIntegrationFixture.Client(http);
        await client.Database(TerraBaseIntegrationFixture.ProjectId, "app").DeployFunctionAsync("hello", new(text));
        await Assert.That(calls).IsEqualTo(1);
    }
}
