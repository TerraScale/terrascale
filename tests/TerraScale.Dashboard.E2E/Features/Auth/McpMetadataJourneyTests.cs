using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.E2E.Features.Auth
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class McpMetadataJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task MalformedUnicodeIsAnOAuth400BeforeAnyPersistentWrite()
        {
            await using var scenario = await StateSdkScenario.PrepareAsync(fixture.Root);
            await Assert.That(await scenario.StartHostsAsync()).IsTrue();
            using var controls = scenario.CreateProviderClient(); using var client = scenario.CreateClient();
            var fields = new[] { "\"client_name\":\"\\uD800\"", "\"\\uD800\":\"name\"", "\"ignored\":\"\\uD800\"", "\"client_name\":\"x\"" };
            foreach (var field in fields)
            {
                var bytes = Encoding.UTF8.GetBytes("{" + field + ",\"redirect_uris\":[\"http://127.0.0.1:33418/callback\"]}");
                if (field.EndsWith("\"x\"", StringComparison.Ordinal))
                {
                    bytes[Array.IndexOf(bytes, (byte)'x')] = 0xff;
                }

                using var content = new ByteArrayContent(bytes); content.Headers.ContentType = new("application/json");
                using var response = await client.PostAsync("/oauth/register", content);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
                var error = await response.Content.ReadFromJsonAsync<JsonElement>();
                await Assert.That(error.GetProperty("error").GetString()).IsEqualTo("invalid_client_metadata");
            }
            var observed = await controls.GetFromJsonAsync<JsonElement>("/__test/observations");
            await Assert.That(observed.GetProperty("documentWrites").GetInt32()).IsEqualTo(0);
            await Assert.That(observed.GetProperty("collections").EnumerateArray().Single(c => c.GetProperty("name").GetString() == McpOAuth.ClientCollection).GetProperty("documents").GetInt32()).IsEqualTo(0);
        }
    }
}
