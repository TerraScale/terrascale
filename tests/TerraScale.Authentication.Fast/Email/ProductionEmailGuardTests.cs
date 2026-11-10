using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Email;

public sealed class ProductionEmailGuardTests
{
    [Test]
    public async Task Production_without_an_email_sender_refuses_to_start()
    {
        var error = await Assert.That(async () => await AuthTestServer.StartAsync(settings =>
        {
            settings["environment"] = "Production";
            settings["public-origin"] = AuthOptions.GlobalOrigin;
            settings["terrabase-url"] = "https://storage.example.invalid";
        })).Throws<AuthConfigurationException>();

        await Assert.That(error!.Message).IsEqualTo("TS_AUTH_SMTP_HOST, TS_AUTH_SMTP_USERNAME, TS_AUTH_SMTP_PASSWORD, CLOUDFLARE_ACCOUNT_ID and CLOUDFLARE_EMAIL_TOKEN are required outside Development");
    }
}
