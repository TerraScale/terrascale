using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.FastTests;

/// <summary>
/// The demo backend (TS_DASHBOARD_PRODUCTION_DEMO=1) keeps accounts, sessions,
/// and the onboarding wizard in process memory. If Fly stops the machine on
/// idle, or runs more than one, the machine that answers the next request has
/// an empty store: onboarding's final step lands on the protected "/" route,
/// bounces to /login, and the credentials created a moment earlier are
/// rejected. These guards keep every deploy pinned to one warm machine.
///
/// Every match is anchored to an active (non-comment) configuration or command
/// line, so a comment or a reformatted value can neither spoof the guard nor
/// make it fail spuriously.
/// </summary>
public sealed class DemoBackendSingleMachineTests
{
    [Test]
    public async Task FlyConfigKeepsTheInMemoryDemoStoreWarm()
    {
        var flyToml = await File.ReadAllTextAsync(RepoFile("fly.toml"));

        // Capture the value on the active line instead of substring-matching the
        // file: comments and reformatting cannot spoof the guard.
        var autoStop = Regex.Match(flyToml, @"^\s*auto_stop_machines\s*=\s*[""'](?<value>[^""']*)[""']", RegexOptions.Multiline);
        await Assert.That(autoStop.Success).IsTrue();
        await Assert.That(autoStop.Groups["value"].Value).IsEqualTo("off");

        var minRunning = Regex.Match(flyToml, @"^\s*min_machines_running\s*=\s*(?<value>\d+)", RegexOptions.Multiline);
        await Assert.That(minRunning.Success).IsTrue();
        await Assert.That(minRunning.Groups["value"].Value).IsEqualTo("1");
    }

    [Test]
    public async Task DeployWorkflowsPinASingleMachine()
    {
        foreach (var workflow in new[] { ".forgejo/workflows/deploy.yml", ".forgejo/workflows/preview.yml" })
        {
            var text = await File.ReadAllTextAsync(RepoFile(workflow));
            var deploysWithoutSpares = Regex.IsMatch(text, @"^\s*fly deploy\b.*--ha=false", RegexOptions.Multiline);
            // (?<!\d)1(?!\d) rejects "10"/"12"/… so a multi-digit scale count
            // can never satisfy the guard.
            var pinsOneMachine = Regex.IsMatch(text, @"^\s*fly scale count\s+(?<!\d)1(?!\d)", RegexOptions.Multiline);
            if (!deploysWithoutSpares || !pinsOneMachine)
            {
                throw new InvalidOperationException(
                    $"{workflow} must run 'fly deploy --ha=false' and 'fly scale count 1' so the in-memory demo store stays on one warm machine.");
            }
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

        var fullPath = Path.Combine(directory.FullName, relativePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Guarded configuration file is missing: {fullPath}", fullPath);
        }

        return fullPath;
    }
}
