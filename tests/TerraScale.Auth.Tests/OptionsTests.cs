namespace TerraScale.Auth.Tests;

[NotInParallel]
public sealed class OptionsTests
{
    [Test]
    public async Task RequiredVariablesThrowActionableErrors()
    {
        var missing = Assert.Throws<InvalidOperationException>(() => TerraScale.Auth.Configuration.AuthOptions.Load([]));
        await Assert.That(missing.Message).Contains("TS_AUTH_PUBLIC_ORIGIN");
    }

    [Test]
    public async Task ArgsOverrideEnvironment()
    {
        Environment.SetEnvironmentVariable("TS_AUTH_PUBLIC_ORIGIN", "https://env.example");
        try
        {
            var options = TerraScale.Auth.Configuration.AuthOptions.Load(
            [
                "--public-origin=https://args.example",
                "--terrascale-api-key=k",
                "--database=ts-auth",
                "--master-key=" + Convert.ToBase64String(new byte[32]),
            ]);
            await Assert.That(options.PublicOrigin.Host).IsEqualTo("args.example");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TS_AUTH_PUBLIC_ORIGIN", null);
        }
    }
}
