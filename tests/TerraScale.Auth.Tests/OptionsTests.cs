namespace TerraScale.Auth.Tests;

[NotInParallel]
public sealed class OptionsTests
{
    [Test]
    public async Task RequiredVariablesThrowActionableErrors()
    {
        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(k => k.StartsWith("TS_AUTH_", StringComparison.Ordinal)).ToList())
            Environment.SetEnvironmentVariable(name, null);
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
                "--operator-key=op",
            ]);
            await Assert.That(options.PublicOrigin.Host).IsEqualTo("args.example");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TS_AUTH_PUBLIC_ORIGIN", null);
        }
    }
}
