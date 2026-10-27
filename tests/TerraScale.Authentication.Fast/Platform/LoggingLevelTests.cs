using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Platform;

public sealed class LoggingLevelTests
{
    [Test]
    [Arguments("OpenIddict.Server.OpenIddictServerDispatcher", false)]
    [Arguments("OpenIddict.Validation", false)]
    [Arguments("Microsoft.AspNetCore.Hosting.Diagnostics", false)]
    [Arguments("Microsoft.Extensions.Http.DefaultHttpClientFactory", false)]
    [Arguments("Microsoft.Hosting.Lifetime", true)]
    [Arguments("TerraScale.Authentication.Features.Email.CloudflareEmailSender", true)]
    public async Task Production_preserves_email_outcomes_and_startup_but_suppresses_framework_information(string category, bool information)
    {
        await using var server = await AuthTestServer.StartAsync(settings: values =>
        {
            values.Remove("environment");
            values["terrabase-url"] = "http://127.0.0.1:1";
            values["cloudflare-account-id"] = "0123456789abcdef0123456789abcdef";
            values["cloudflare-email-token"] = "synthetic-email-token";
        }, configure: builder =>
        {
            builder.Configuration["Logging:LogLevel:OpenIddict"] = "Trace";
            builder.Configuration["Logging:LogLevel:Microsoft"] = "Trace";
        });
        var logger = server.App.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category);

        await Assert.That(logger.IsEnabled(LogLevel.Debug)).IsFalse();
        await Assert.That(logger.IsEnabled(LogLevel.Information)).IsEqualTo(information);
        await Assert.That(logger.IsEnabled(LogLevel.Warning)).IsTrue();
        await Assert.That(logger.IsEnabled(LogLevel.Error)).IsTrue();
    }
}
