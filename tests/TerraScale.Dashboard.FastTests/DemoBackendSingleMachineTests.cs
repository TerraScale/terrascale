namespace TerraScale.Dashboard.FastTests;

/// <summary>
/// The demo backend (TS_DASHBOARD_PRODUCTION_DEMO=1) keeps accounts, sessions,
/// and the onboarding wizard in process memory. If Fly stops the machine on
/// idle, or runs more than one, the machine that answers the next request has
/// an empty store: onboarding's final step lands on the protected "/" route,
/// bounces to /login, and the credentials created a moment earlier are
/// rejected. These guards keep every deploy pinned to one warm machine.
/// </summary>
public sealed class DemoBackendSingleMachineTests
{
    [Test]
    public async Task FlyConfigKeepsTheInMemoryDemoStoreWarm()
    {
        var flyToml = await File.ReadAllTextAsync(RepoFile("fly.toml"));
        await Assert.That(flyToml.Contains("auto_stop_machines = \"stop\"")).IsFalse();
        await Assert.That(flyToml.Contains("auto_stop_machines = \"off\"")).IsTrue();
        await Assert.That(flyToml.Contains("min_machines_running = 1")).IsTrue();
    }

    [Test]
    public async Task DeployWorkflowsPinASingleMachine()
    {
        foreach (var workflow in new[] { ".forgejo/workflows/deploy.yml", ".forgejo/workflows/preview.yml" })
        {
            var text = await File.ReadAllTextAsync(RepoFile(workflow));
            await Assert.That(text.Contains("--ha=false")).IsTrue();
            await Assert.That(text.Contains("fly scale count 1")).IsTrue();
        }
    }

    private static string RepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TerraScale.Dashboard.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException($"Could not locate the repository root from {AppContext.BaseDirectory}.");
        }

        return Path.Combine(directory.FullName, relativePath);
    }
}
