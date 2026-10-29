using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Slow.Identity;

/// <summary>Actual hosted mail producers preserve the request language and the resolved tenant.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class HostedEmailLocaleTests(TerraBaseFixture fixture)
{
    [Test]
    [Arguments("sign-up", false, null, false)]
    [Arguments("sign-up", false, "pt-BR,pt;q=0.9,en;q=0.8", false)]
    [Arguments("sign-up", true, null, false)]
    [Arguments("sign-up", true, "pt-BR,pt;q=0.9,en;q=0.8", false)]
    [Arguments("reset", true, null, false)]
    [Arguments("reset", true, "pt-BR,pt;q=0.9,en;q=0.8", false)]
    [Arguments("sign-up", false, null, true)]
    [Arguments("sign-up", false, "pt-BR,pt;q=0.9,en;q=0.8", true)]
    [Arguments("sign-up", true, null, true)]
    [Arguments("sign-up", true, "pt-BR,pt;q=0.9,en;q=0.8", true)]
    [Arguments("reset", true, null, true)]
    [Arguments("reset", true, "pt-BR,pt;q=0.9,en;q=0.8", true)]
    public async Task Signup_and_reset_send_redeemable_codes_in_the_hosted_language(string action, bool existing, string? acceptLanguage, bool projectTenant)
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var faker = new Faker();
        var tenant = OidcIssuer.InstanceId;
        if (projectTenant)
        {
            var (_, token) = await oidc.SignInUserAsync(faker.Internet.Email().ToLowerInvariant());
            var organization = await oidc.CreateOrganizationAsync(token);
            tenant = await oidc.CreateProjectAsync(token, organization, "TerraScale");
        }

        var address = faker.Internet.Email(provider: "example.test").ToLowerInvariant();
        if (existing)
        {
            await Assert.That(await oidc.Server.App.Services.GetRequiredService<Users>().CreateAsync(tenant, address)).IsNotNull();
        }

        var form = new Dictionary<string, string> { ["email"] = address, ["password"] = "Synthetic-password-123!" };
        using var request = Form($"/ui/{tenant}/{action}", form, acceptLanguage);
        using var response = await oidc.Client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var (recipient, message) = await oidc.Email.NextAsync();
        var code = SixDigits().Match(message.Text).Value;
        var portuguese = acceptLanguage is not null;
        var kind = action == "reset" ? AuthEmailKind.PasswordReset : existing ? AuthEmailKind.SignInCode : AuthEmailKind.VerifyEmail;
        var subject = (kind, portuguese) switch
        {
            (AuthEmailKind.PasswordReset, true) => "é o seu código para redefinir a senha de TerraScale",
            (AuthEmailKind.PasswordReset, false) => "is your TerraScale password reset code",
            (AuthEmailKind.SignInCode, true) => "é o seu código de acesso para TerraScale",
            (AuthEmailKind.SignInCode, false) => "is your TerraScale sign-in code",
            (_, true) => "é o seu código de verificação para TerraScale",
            _ => "is your TerraScale verification code",
        };
        await Assert.That(recipient).IsEqualTo(address);
        await Assert.That(code).IsNotEmpty();
        await Assert.That(message.Subject).IsEqualTo($"{code} {subject}");
        await Assert.That(message.Html).Contains(portuguese ? "<html lang=\"pt-BR\"" : "<html lang=\"en\"");
        await Assert.That(message.Text).Contains(portuguese ? "30 minutos" : "30 minutes");
        await Assert.That(message.Tenant).IsEqualTo(tenant);
        await Assert.That(message.Purpose).IsEqualTo(EmailPurpose.Authentication);

        form["code"] = code;
        form["pending_token"] = PendingToken().Match(await response.Content.ReadAsStringAsync()).Groups[1].Value;
        using var redeemed = await oidc.PostFormAsync($"/ui/{tenant}/{action}/{(action == "reset" ? "complete" : "verify")}", form);
        await Assert.That(redeemed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        if (action == "sign-up")
        {
            await Assert.That(redeemed.Headers.GetValues("Set-Cookie").Any(cookie => cookie.StartsWith(Tenancy.SessionCookie(tenant) + "=", StringComparison.Ordinal))).IsTrue();
        }
        else
        {
            using var password = await oidc.PostFormAsync($"/ui/{tenant}/sign-in/password", form);
            await Assert.That(password.Headers.GetValues("Set-Cookie").Any(cookie => cookie.StartsWith(Tenancy.SessionCookie(tenant) + "=", StringComparison.Ordinal))).IsTrue();
        }
    }

    [Test]
    [Arguments("sign-in/password", true)]
    [Arguments("sign-in/verify", true)]
    [Arguments("sign-up/verify", true)]
    [Arguments("reset/complete", true)]
    [Arguments("sign-in/password", false)]
    [Arguments("sign-in/verify", false)]
    [Arguments("sign-up/verify", false)]
    [Arguments("reset/complete", false)]
    public async Task Project_lockout_notices_keep_tenant_branding_locale_and_notification_purpose(string action, bool branded)
    {
        var clock = new ManualClock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var faker = new Faker();
        var (_, token) = await oidc.SignInUserAsync(faker.Internet.Email().ToLowerInvariant());
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Default project");
        var other = await oidc.CreateProjectAsync(token, organization, "Other tenant brand");
        var name = branded ? "Project mail brand" : "Default project";
        if (branded)
        {
            await oidc.OkAsync(HttpMethod.Put, $"/v1/{project}/settings", token, new
            {
                branding = new { name, logo_url = "https://cdn.example.test/project.png", email_message = "Project-only footer" },
            });
        }

        var saved = await oidc.Server.App.Services.GetRequiredService<EmailSettings>().SaveAsync(OidcIssuer.InstanceId, 0,
            new EmailActor("synthetic-admin"), "branding", config => config with
            {
                Branding = new EmailBranding { BrandName = "Platform-only brand", SenderName = "Platform-only sender", FooterText = "Platform-only footer", ReplyTo = "support@example.test" },
                Templates = [new EmailTemplateOverride("lockout", AuthEmails.Portuguese, new EmailTemplateFields(Subject: "Platform-only template"))],
            });
        await Assert.That(saved).IsNotNull();
        var address = faker.Internet.Email(provider: "example.test").ToLowerInvariant();
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        foreach (var tenant in new[] { project, other, OidcIssuer.InstanceId })
        {
            await Assert.That(await users.CreateAsync(tenant, address)).IsNotNull();
        }

        var form = new Dictionary<string, string> { ["email"] = address, ["password"] = "Wrong-password-123!", ["code"] = "not-a-code" };
        var maximum = action == "sign-in/password" ? HostedAuth.PasswordPerEmail : HostedAuth.CodePerEmail;
        var window = action == "sign-in/password" ? HostedAuth.PasswordWindow : HostedAuth.CodeWindow;
        var seconds = (long)window.TotalSeconds;
        var deadline = DateTimeOffset.FromUnixTimeSeconds((clock.GetUtcNow().ToUnixTimeSeconds() / seconds + 1) * seconds);
        for (var attempt = 0; attempt < maximum; attempt++)
        {
            using var admitted = await oidc.PostFormAsync($"/ui/{project}/{action}", form);
            await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        await Assert.That(oidc.Email.HasPending).IsFalse();
        for (var refusal = 0; refusal < 2; refusal++)
        {
            using var request = Form($"/ui/{project}/{action}", form, "pt-BR,pt;q=0.9,en;q=0.8");
            using var locked = await oidc.Client.SendAsync(request);
            await Assert.That(locked.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(locked.Headers.RetryAfter!.Delta).IsEqualTo(deadline - clock.GetUtcNow());
            await Assert.That(OidcHarness.SessionCookie(locked)).IsNull();
        }

        // The same address in other tenants still has its own attempt budget and sends no notice.
        foreach (var tenant in new[] { other, OidcIssuer.Slug })
        {
            using var isolated = await oidc.PostFormAsync($"/ui/{tenant}/{action}", form);
            await Assert.That(isolated.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        await oidc.Server.App.StopAsync();
        var (recipient, message) = await oidc.Email.NextAsync();
        await Assert.That(recipient).IsEqualTo(address);
        await Assert.That(message.Tenant).IsEqualTo(project);
        await Assert.That(message.Purpose).IsEqualTo(EmailPurpose.Notification);
        await Assert.That(message.FromName).IsEqualTo("TerraScale");
        await Assert.That(message.ReplyTo).IsNull();
        await Assert.That(message.Subject).IsEqualTo($"Pausa temporária de autenticação em {name}");
        await Assert.That(message.Html).Contains("<html lang=\"pt-BR\"");
        await Assert.That(message.Text).Contains(deadline.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
        await Assert.That(message.Text).Contains("Sent by TerraScale Authentication.").And.DoesNotContain("Platform-only").And.DoesNotContain("Other tenant brand");
        await Assert.That(message.Html).DoesNotContain("Platform-only").And.DoesNotContain("Other tenant brand");
        if (branded)
        {
            await Assert.That(message.Html).Contains("src=\"https://cdn.example.test/project.png\"");
            await Assert.That(message.Text).Contains("Project-only footer");
        }

        await Assert.That(oidc.Email.HasPending).IsFalse();
    }

    private static HttpRequestMessage Form(string path, Dictionary<string, string> fields, string? acceptLanguage)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Add("Origin", OidcHarness.PublicOrigin);
        if (acceptLanguage is not null)
        {
            request.Headers.Add("Accept-Language", acceptLanguage);
        }

        return request;
    }

    [GeneratedRegex(@"\b[0-9]{6}\b")]
    private static partial Regex SixDigits();

    [GeneratedRegex("name=\"pending_token\" value=\"([^\"]+)\"")]
    private static partial Regex PendingToken();
}
