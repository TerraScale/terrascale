using TerraBase.Configuration;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class ConfigurationTests
{

    [Test]
    public async Task Production_trusts_only_the_one_platform_issuer_and_audience_ts_auth_issues_in_every_region()
    {
        static Dictionary<string, string> Auth(string issuer, string audience)
        {
            return new() { ["auth-issuer"] = issuer, ["auth-audience"] = audience };
        }
        var production = TerraBaseOptions.FromValues(Auth(TerraBaseOptions.PlatformAuthIssuer, TerraBaseOptions.PlatformAuthAudience), production: true);
        await Assert.That((production.AuthIssuer, production.AuthAudiences.Single())).IsEqualTo(("https://auth.terrascale.tech/terrascale", "https://api.terrascale.tech"));
        foreach (var (issuer, audience) in new[]
        {
            ("https://attacker.example/terrascale", TerraBaseOptions.PlatformAuthAudience),
            ("https://gru.auth.terrascale.tech/terrascale", TerraBaseOptions.PlatformAuthAudience),
            ("https://auth.terrascale.tech", TerraBaseOptions.PlatformAuthAudience),
            (TerraBaseOptions.PlatformAuthIssuer, "https://gru.api.terrascale.tech"),
        })
        {
            var refused = await Assert.That(() => TerraBaseOptions.FromValues(Auth(issuer, audience), production: true)).Throws<TerraBaseConfigurationException>();
            await Assert.That(refused!.Message).IsEqualTo("outside Development, auth-issuer must be https://auth.terrascale.tech/terrascale and auth-audience must include https://api.terrascale.tech: ts-auth issues every region's platform tokens with that pair");
        }
        var development = TerraBaseOptions.FromValues(Auth("https://127.0.0.1:5001/terrascale", "terrabase-test"));
        await Assert.That(development.AuthIssuer).IsEqualTo("https://127.0.0.1:5001/terrascale");
    }

}
