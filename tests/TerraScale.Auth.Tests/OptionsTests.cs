namespace TerraScale.Auth.Tests;

[NotInParallel]
public sealed class OptionsTests
{
    /// <summary>Snapshots every TS_AUTH_* variable: these tests clear or overwrite the
    /// block AuthOptions boots from, and must hand back what the fixtures set.</summary>
    private static IReadOnlyDictionary<string, string?> SnapshotAuthEnvironment() =>
        Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(k => k.StartsWith("TS_AUTH_", StringComparison.Ordinal))
            .ToDictionary(k => k, k => Environment.GetEnvironmentVariable(k));

    private static void RestoreAuthEnvironment(IReadOnlyDictionary<string, string?> snapshot)
    {
        foreach (var (name, value) in snapshot)
            Environment.SetEnvironmentVariable(name, value);
    }

    [Test]
    public async Task RequiredVariablesThrowActionableErrors()
    {
        var snapshot = SnapshotAuthEnvironment();
        try
        {
            foreach (var name in snapshot.Keys)
                Environment.SetEnvironmentVariable(name, null);
            var missing = Assert.Throws<InvalidOperationException>(() => TerraScale.Auth.Configuration.AuthOptions.Load([]));
            await Assert.That(missing.Message).Contains("TS_AUTH_PUBLIC_ORIGIN");
        }
        finally
        {
            RestoreAuthEnvironment(snapshot);
        }
    }

    [Test]
    public async Task OversizedMasterKeyIsRejectedAtLoad()
    {
        // 48 bytes decodes fine but is not a valid AES-GCM key size; accepting it
        // would crash every SecretBox call at runtime instead of failing at startup.
        Assert.Throws<InvalidOperationException>(() => TerraScale.Auth.Configuration.AuthOptions.Load(
        [
            "--public-origin=https://example.com",
            "--terrascale-api-key=k",
            "--database=ts-auth",
            "--master-key=" + Convert.ToBase64String(new byte[48]),
            "--operator-key=op",
        ]));
    }

    [Test]
    public async Task ArgsOverrideEnvironment()
    {
        var snapshot = SnapshotAuthEnvironment();
        try
        {
            Environment.SetEnvironmentVariable("TS_AUTH_PUBLIC_ORIGIN", "https://env.example");
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
            RestoreAuthEnvironment(snapshot);
        }
    }
}
