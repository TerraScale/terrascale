using System.Net;
using System.Text.Json;

namespace TerraScale.Dashboard.E2E;

[ClassDataSource<DashboardE2EFixture>]
public sealed class ProfileFeedbackJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    [Arguments(false, "profile-saved", "Profile saved.")]
    [Arguments(true, "language-saved", "Language preference saved.")]
    public async Task SuccessfulSaveRedirectsToAnAnnouncedConfirmationAndPersistsTheChange(bool language, string status, string message)
    {
        using var client = fixture.CreateClient();
        using var login = await HttpJourney.LoginAsync(client);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var form = new Dictionary<string, string> { ["_csrf"] = await HttpJourney.CsrfAsync(client) };
        if (language) form["locale"] = "pt-BR";
        else { form["name"] = "Updated profile"; form["phoneNumber"] = "+5511999990000"; }
        using var saved = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/profile" + (language ? "/language" : ""), form);
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(saved.Headers.Location!.OriginalString).IsEqualTo("/profile?tab=profile&status=" + status);
        using var confirmation = await client.GetAsync(saved.Headers.Location);
        await Assert.That(confirmation.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await confirmation.Content.ReadAsStringAsync()).Contains("role=\"status\" aria-live=\"polite\" class=\"ts-alert ts-alert-success\">" + message);
        using var current = await client.GetAsync("/api/v1/auth/me");
        using var identity = JsonDocument.Parse(await current.Content.ReadAsStringAsync());
        await Assert.That(identity.RootElement.GetProperty(language ? "locale" : "name").GetString()).IsEqualTo(language ? "pt-BR" : "Updated profile");
        if (!language) await Assert.That(identity.RootElement.GetProperty("phoneNumber").GetString()).IsEqualTo("+5511999990000");
    }

    [Test]
    public async Task RejectedLanguageRetainsThePreferenceAndDoesNotAnnounceSuccess()
    {
        using var client = fixture.CreateClient();
        using var login = await HttpJourney.LoginAsync(client);
        using var before = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
        using var rejected = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/profile/language", new()
        {
            ["_csrf"] = await HttpJourney.CsrfAsync(client),
            ["locale"] = "unsupported"
        });
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        var html = await rejected.Content.ReadAsStringAsync();
        await Assert.That(html).Contains("Choose an available language.");
        await Assert.That(html.Contains("ts-alert-success", StringComparison.Ordinal)).IsFalse();
        using var after = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
        await Assert.That(after.RootElement.GetProperty("locale").GetString()).IsEqualTo(before.RootElement.GetProperty("locale").GetString());
    }

    [Test]
    [Arguments("/profile")]
    [Arguments("/profile?status=untrusted-message")]
    [Arguments("/profile?tab=sessions&status=profile-saved")]
    public async Task OtherPagesAndUnknownStatusNeverShowASavedConfirmation(string path)
    {
        using var client = fixture.CreateClient();
        using var login = await HttpJourney.LoginAsync(client);
        var html = await client.GetStringAsync(path);
        await Assert.That(html.Contains("ts-alert-success", StringComparison.Ordinal)).IsFalse();
    }
}
