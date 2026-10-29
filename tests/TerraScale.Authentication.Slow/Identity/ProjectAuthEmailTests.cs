using System.Net;
using System.Text.RegularExpressions;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Slow.Identity;

/// <summary>Project authentication mail through the hosted HTTP flows and persistent tenant settings.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class ProjectAuthEmailTests(TerraBaseFixture fixture)
{
    [Test]
    [Arguments("sign-in", true)]
    [Arguments("reset", true)]
    [Arguments("sign-up", true)]
    [Arguments("sign-in", false)]
    [Arguments("reset", false)]
    [Arguments("sign-up", false)]
    public async Task Project_codes_use_the_project_brand_with_a_fixed_platform_sender(string action, bool branded)
    {
        var faker = new Faker();
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, token) = await oidc.SignInUserAsync(faker.Internet.Email().ToLowerInvariant());
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Unbranded project");
        var name = branded ? "<b>Acme & Co</b>" : "Unbranded project";
        if (branded)
        {
            await oidc.OkAsync(HttpMethod.Put, $"/v1/{project}/settings", token, new
            {
                branding = new { name, logo_url = "https://cdn.example.test/mail.png", theme_color = "#123456", email_message = "<script>mail()</script>\n{{ code }}" },
            });
        }

        var address = faker.Internet.Email(provider: "example.test").ToLowerInvariant();
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        if (action != "sign-up")
        {
            await users.CreateAsync(project, address);
        }

        using var requested = await oidc.PostFormAsync($"/ui/{project}/{action}", new() { ["email"] = address, ["password"] = "Synthetic-password-123!" });
        await Assert.That(requested.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var (recipient, message) = await oidc.Email.NextAsync();
        await Assert.That(recipient).IsEqualTo(address);
        await Assert.That(message.Subject).Contains(name).And.Contains(action switch { "reset" => "password reset code", "sign-up" => "verification code", _ => "sign-in code" });
        await Assert.That(message.Text).Contains(name).And.Contains("30 minutes").And.Contains(action == "sign-up" ? "Didn't sign up" : "Never share this code");
        await Assert.That(message.Html).Contains(WebUtility.HtmlEncode(name)).And.DoesNotContain("<b>Acme");
        await Assert.That(message.FromName).IsEqualTo("TerraScale");
        await Assert.That(message.Tenant).IsEqualTo(project);
        await Assert.That(message.Purpose).IsEqualTo(EmailPurpose.Authentication);
        await Assert.That(message.Text).Contains("Sent by TerraScale Authentication.");
        if (branded)
        {
            // Code-only emails show the accent as a tinted code box, rather than an action button.
            await Assert.That(message.Html).Contains("src=\"https://cdn.example.test/mail.png\"")
                .And.Contains("background-color:#eceff1;border:1px solid #c4ccd5;");
            await Assert.That(message.Html).Contains("&lt;script&gt;mail()&lt;/script&gt;").And.DoesNotContain("<script>");
            await Assert.That(message.Text).Contains("<script>mail()</script>\n{{ code }}");
        }

        if (action == "sign-in")
        {
            var other = await oidc.CreateProjectAsync(token, organization, "Other project");
            await users.CreateAsync(other, address);
            var form = new Dictionary<string, string> { ["email"] = address, ["code"] = SixDigits().Match(message.Text).Value };
            using var wrongTenant = await oidc.PostFormAsync($"/ui/{other}/sign-in/verify", form);
            await Assert.That(wrongTenant.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            using var verified = await oidc.PostFormAsync($"/ui/{project}/sign-in/verify", form);
            await Assert.That(verified.Headers.GetValues("Set-Cookie").Any(cookie => cookie.StartsWith($"tsauth_session_{project}=", StringComparison.Ordinal))).IsTrue();
            await oidc.CreateUserAsync(address);
            using var platform = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-in", new() { ["email"] = address });
            await Assert.That(platform.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var (_, platformMessage) = await oidc.Email.NextAsync();
            await Assert.That(platformMessage.Subject).Contains("TerraScale sign-in code");
            await Assert.That(platformMessage.Html).Contains("src=\"https://auth.test.invalid/admin/email-logo.png\"");
            await Assert.That(platformMessage.Text).DoesNotContain(name);
        }
    }

    [GeneratedRegex(@"\b[0-9]{6}\b")]
    private static partial Regex SixDigits();
}
