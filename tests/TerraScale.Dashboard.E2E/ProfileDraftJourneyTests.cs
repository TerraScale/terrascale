using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.E2E
{
    [ClassDataSource<DashboardE2EFixture>]
    public sealed class ProfileDraftJourneyTests(DashboardE2EFixture fixture)
    {
        [Test]
        public async Task RejectedProfileKeepsEscapedSubmittedFieldsWithoutWritingAccountState()
        {
            using var client = fixture.CreateClient();
            using var login = await HttpJourney.LoginAsync(client);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            using var beforeResponse = await client.GetAsync("/api/v1/auth/me");
            using var before = JsonDocument.Parse(await beforeResponse.Content.ReadAsStringAsync());
            var csrf = await HttpJourney.CsrfAsync(client, "/profile");
            foreach (var (name, phone) in new[] { (new string('x', 129) + "\"<script>", "+5511999999999"), ("", new string('9', 65)) })
            {
                using var rejected = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/profile",
                    new() { ["_csrf"] = csrf, ["name"] = name, ["phoneNumber"] = phone });
                await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
                var html = await rejected.Content.ReadAsStringAsync();
                await Assert.That(html.Contains("ts-alert-success", StringComparison.Ordinal)).IsFalse();
                foreach (var (id, expected) in new[] { ("displayName", name), ("phoneNumber", phone) })
                {
                    var field = Regex.Match(html, "<input[^>]*id=\"" + id + "\"[^>]*value=\"([^\"]*)\"");
                    await Assert.That(field.Success).IsTrue();
                    await Assert.That(WebUtility.HtmlDecode(field.Groups[1].Value)).IsEqualTo(expected);
                }
            }
            using var afterResponse = await client.GetAsync("/api/v1/auth/me");
            using var after = JsonDocument.Parse(await afterResponse.Content.ReadAsStringAsync());
            foreach (var field in new[] { "name", "phoneNumber" })
            {
                await Assert.That(after.RootElement.GetProperty(field).GetString()).IsEqualTo(before.RootElement.GetProperty(field).GetString());
            }
        }

        [Test]
        [Arguments("name", "", "displayName", "Enter a display name.")]
        [Arguments("name", "\u0001", "displayName", "Use a valid name with 128 characters or fewer.")]
        [Arguments("phoneNumber", "\u0001", "phoneNumber", "Enter a valid phone number.")]
        public async Task RejectedFieldsIdentifyTheControlAndLeaveStoredProfileUnchanged(string field, string draft, string id, string message)
        {
            using var client = fixture.CreateClient();
            using var login = await HttpJourney.LoginAsync(client);
            var before = await client.GetStringAsync("/api/v1/auth/me");
            var form = new Dictionary<string, string>
            {
                ["_csrf"] = await HttpJourney.CsrfAsync(client),
                ["name"] = "Valid draft",
                ["phoneNumber"] = "+15555550100"
            };
            form[field] = draft;
            using var rejected = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/profile", form);
            await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            var html = await rejected.Content.ReadAsStringAsync();
            var control = Regex.Match(html, "<input[^>]*id=\"" + id + "\"[^>]*>").Value;
            await Assert.That(control).Contains("aria-invalid=\"true\"");
            await Assert.That(control).Contains("aria-describedby=\"" + id + "-error\"");
            await Assert.That(control).Contains("autofocus");
            await Assert.That(html).Contains("id=\"" + id + "-error\">" + message);
            await Assert.That(html).Contains("href=\"#" + id + "\"");
            await Assert.That(html.Contains("ts-alert-success", StringComparison.Ordinal)).IsFalse();
            using var oldIdentity = JsonDocument.Parse(before);
            using var current = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
            foreach (var key in new[] { "name", "phoneNumber" })
            {
                await Assert.That(current.RootElement.GetProperty(key).GetString()).IsEqualTo(oldIdentity.RootElement.GetProperty(key).GetString());
            }
        }
    }
}
