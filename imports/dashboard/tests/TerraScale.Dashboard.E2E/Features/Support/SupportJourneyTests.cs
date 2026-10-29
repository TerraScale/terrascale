using System.Net;
using System.Text.RegularExpressions;
using TerraScale.Dashboard.Features.Support;

namespace TerraScale.Dashboard.E2E.Features.Support;

[ClassDataSource<DashboardE2EFixture>]
public sealed class SupportJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task ContactSupportSendsTheSelectedOrganizationAndKeepsARejectedDraft()
    {
        using var client = fixture.CreateClient();
        using var login = await HttpJourney.LoginAsync(client);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var related = AccountService.NewId();
        var page = await client.GetStringAsync("/support?relatedRequestId=" + related);
        await Assert.That(page).Contains("value=\"" + related + "\"");
        await Assert.That(page).Contains("Contact support").And.Contains("support@terrascale.tech");
        var csrf = Field(page, "_csrf");
        var requestId = Field(page, "requestId");
        var organizationId = Regex.Match(page, "<select[^>]*id=\"support-organization\"[^>]*>\\s*<option value=\"([^\"]+)\"").Groups[1].Value;
        var mail = (Demo.TestSupportMail)fixture.Service<ISupportMail>();
        var form = new Dictionary<string, string>
        {
            ["_csrf"] = csrf,
            ["requestId"] = requestId,
            ["relatedRequestId"] = related,
            ["organizationId"] = organizationId,
            ["projectId"] = "",
            ["databaseId"] = "",
            ["severity"] = "urgent",
            ["subject"] = "two\nlines",
            ["message"] = "Writes fail <b>since</b> 09:00 UTC."
        };

        using var rejected = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/support", form);
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        var html = await rejected.Content.ReadAsStringAsync();
        await Assert.That(html).Contains("Writes fail &lt;b&gt;since&lt;/b&gt; 09:00 UTC.");
        await Assert.That(mail.Sent.Any(sent => sent.RequestId == requestId)).IsFalse();

        form["subject"] = "Writes fail";
        using var sent = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/support", form);
        await Assert.That(sent.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(sent.Headers.Location!.OriginalString).IsEqualTo("/support?sent=" + requestId[^8..]);
        var request = mail.Sent.Single(item => item.RequestId == requestId);
        await Assert.That(request.OrganizationId).IsEqualTo(organizationId);
        await Assert.That(request.Message).Contains("Request ID: " + related);
        await Assert.That(request.Severity).IsEqualTo("urgent");
        await Assert.That(request.ProjectId).IsNull();
        await Assert.That(await client.GetStringAsync("/support?sent=" + requestId[^8..])).Contains("Request " + requestId[^8..] + " sent");

        form["organizationId"] = "01JA0000000000000000000000";
        using var foreign = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/support", form);
        await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
    }

    private static string Field(string html, string name) =>
        WebUtility.HtmlDecode(Regex.Match(html, "<input type=\"hidden\" name=\"" + name + "\" value=\"([^\"]+)\"").Groups[1].Value);
}
