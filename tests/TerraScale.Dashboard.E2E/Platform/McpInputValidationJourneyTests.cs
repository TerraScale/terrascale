using System.Net;

namespace TerraScale.Dashboard.E2E.Platform
{
    [ClassDataSource<DashboardE2EFixture>]
    public sealed class McpInputValidationJourneyTests(DashboardE2EFixture fixture)
    {
        [Test]
        public async Task MalformedCallsNameTheProblemInsteadOfFailingGenericallyOrSilently()
        {
            using var client = fixture.CreateClient();
            using var login = await HttpJourney.LoginAsync(client);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var secret = await HttpJourney.CreatePatAsync(client, "MCP input validation");

            // tools/list never issues a cursor, so any cursor is refused rather than hiding every tool.
            foreach (var cursor in new[] { "bogus-cursor", "" })
            {
                await Assert.That((await HttpJourney.McpAsync(client, secret, "tools/list", new { cursor })).GetProperty("error").GetProperty("code").GetInt32()).IsEqualTo(-32602);
            }

            var arguments = await HttpJourney.McpAsync(client, secret, "tools/call", new { name = "get_context", arguments = "notanobject" });
            await Assert.That(arguments.GetProperty("error").GetProperty("code").GetInt32()).IsEqualTo(-32602);

            await Assert.That(await ErrorAsync("get_document", new { databaseId = "01JA0000000000000000000001" })).IsEqualTo("Missing required argument 'collection'.");
            await Assert.That(await ErrorAsync("create_document", new { databaseId = "01JA0000000000000000000001", collection = "items", document = "notobject" }))
                .IsEqualTo("Argument 'document' must be an object.");
            await Assert.That(await ErrorAsync("list_documents", new { databaseId = "01JA0000000000000000000001", collection = "items", limit = "many" }))
                .IsEqualTo("Argument 'limit' must be an integer.");
            await Assert.That(await ErrorAsync("get_overview", new { range = "bogus" })).IsEqualTo("Choose 1h, 24h, 7d or 30d.");
            await Assert.That(await ErrorAsync("save_onboarding_step", new { step = "nope" })).IsEqualTo("Choose a valid setup step.");

            await Assert.That(await ErrorAsync("update_profile", new { phone = "bad phone" })).IsEqualTo("Enter a valid phone number.");
            await Assert.That(await ErrorAsync("update_profile", new { name = "" })).IsEqualTo("Enter a display name.");
            var saved = await HttpJourney.McpAsync(client, secret, "tools/call", new { name = "update_profile", arguments = new { phone = "+55 (11) 99999-0000" } });
            await Assert.That(saved.GetProperty("result").GetProperty("structuredContent").GetProperty("saved").GetBoolean()).IsTrue();
            var profile = await HttpJourney.McpAsync(client, secret, "tools/call", new { name = "get_profile", arguments = new { } });
            await Assert.That(profile.GetProperty("result").GetProperty("structuredContent").GetProperty("phone").GetString()).IsEqualTo("+55 (11) 99999-0000");

            async Task<string> ErrorAsync(string name, object values)
            {
                var response = await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments = values });
                var result = response.GetProperty("result");
                await Assert.That(result.GetProperty("isError").GetBoolean()).IsTrue().Because(response.ToString());
                return result.GetProperty("content")[0].GetProperty("text").GetString()!;
            }
        }
    }
}
