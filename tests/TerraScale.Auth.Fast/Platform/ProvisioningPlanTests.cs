using System.Diagnostics;
using TerraScale.Auth.Store;

namespace TerraScale.Auth.Fast.Platform;

public sealed class ProvisioningPlanTests
{
    [Test]
    public async Task Provisioning_plan_names_the_production_dataset_without_executing_changes()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TerraScale.Auth.slnx")))
        {
            directory = directory.Parent;
        }

        var repo = directory?.FullName ?? throw new InvalidOperationException("Run the fast suite from inside the ts-auth checkout.");
        using var process = Process.Start(new ProcessStartInfo("bash", [Path.Combine(repo, "scripts", "provision.sh")])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr);
            await Assert.That(process.ExitCode).IsEqualTo(0).Because(await stderr);
            var plan = await stdout;
            await Assert.That(plan).Contains("Provisioning plan (no changes executed):");
            await Assert.That(plan).Contains("organization terrascale");
            await Assert.That(plan).Contains("ts-auth project");
            await Assert.That(plan).Contains("auth database");
            await Assert.That(plan).Contains("TS_AUTH_DATABASE=<ts-auth-project-id>/auth");
            foreach (var collection in AuthStore.RequiredCollections)
            {
                await Assert.That(plan).Contains(collection);
            }
            await Assert.That(plan).DoesNotContain("terrascale" + "-internal");
            await Assert.That(plan).DoesNotContain("its auth project");
            await Assert.That(plan).DoesNotContain("<auth-project-id>");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
