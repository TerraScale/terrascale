namespace TerraScale.DotNet.Tests;

public sealed class WorkflowTriggerTests
{
    [Test]
    public async Task DevelopPushDoesNotTriggerAutomation()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, ".forgejo", "workflows", "ci.yml")))
        {
            root = root.Parent;
        }

        await Assert.That(root).IsNotNull();
        var workflow = (await File.ReadAllTextAsync(
            Path.Combine(root!.FullName, ".forgejo", "workflows", "ci.yml"))).ReplaceLineEndings("\n");

        await Assert.That(workflow).Contains("  push:\n    branches: [main]");
    }
}
