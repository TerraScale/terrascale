using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.E2E;

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
            await Assert.That(after.RootElement.GetProperty(field).GetString()).IsEqualTo(before.RootElement.GetProperty(field).GetString());
    }
}
