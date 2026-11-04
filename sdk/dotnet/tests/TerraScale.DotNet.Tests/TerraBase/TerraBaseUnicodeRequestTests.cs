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
        foreach (var operation in new[] { "function", "query" })
        {
            var text = new string(character, operation == "function" ? 200000 : 9000);
            var calls = 0;
            using var handler = new TerraBaseTestHandler(async (request, token) =>
            {
                calls++;
                var bytes = await request.Content!.ReadAsByteArrayAsync(token);
                using var sent = JsonDocument.Parse(bytes);
                await Assert.That(sent.RootElement.GetProperty(operation == "function" ? "source" : "sql").GetString()).IsEqualTo(text);
                // Default producer-style escaping must not become the request's size.
                await Assert.That(bytes.Length <= Encoding.UTF8.GetByteCount(text) + 200).IsTrue();
                await Assert.That(request.Headers.Authorization!.ToString()).IsEqualTo("Bearer project-user-token");
                await Assert.That(request.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");
                return TerraBaseIntegrationFixture.Json(operation == "function"
                    ? """{"name":"hello","createdAt":"2026-10-26T00:00:00Z","updatedAt":"2026-10-26T00:00:00Z"}"""
                    : """{"id":"01JA0000000000000000000002","name":"recent","sql":"SELECT 1","createdAt":"2026-10-26T00:00:00Z","updatedAt":null}""");
            });
            using var http = new HttpClient(handler);
            var client = TerraBaseIntegrationFixture.Client(http);
            if (operation == "function")
                await client.Database(TerraBaseIntegrationFixture.ProjectId, "app").DeployFunctionAsync("hello", new(text));
            else
                await client.Project(TerraBaseIntegrationFixture.ProjectId).CreateSavedQueryAsync("recent", text);
            await Assert.That(calls).IsEqualTo(1);
        }
    }
}
