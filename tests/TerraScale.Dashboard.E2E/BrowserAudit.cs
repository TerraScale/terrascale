using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E;

// Shared helpers for the C# browser journeys (formerly node + playwright-core + axe-core).
public static class BrowserAudit
{
    private static readonly AxeRunOptions Wcag22AA = new()
    {
        RunOnly = new RunOnlyOptions { Type = "tag", Values = ["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"] },
    };

    // Fails with each violating rule and its node targets, like the former assert.deepEqual(violations, []).
    public static async Task AssertAccessibleAsync(IPage page, string because)
    {
        var result = await page.RunAxe(Wcag22AA);
        var violations = result.Violations.Select(v => v.Id + ": " + string.Join(", ", v.Nodes.Select(n => n.Target.ToString()))).ToArray();
        await Assert.That(violations).IsEmpty().Because(because);
    }

    // test-results/dotnet/<name>, created on demand; screenshots and reports land here.
    public static string Output(string root, string name)
    {
        var directory = Path.Combine(root, "test-results", "dotnet", name);
        Directory.CreateDirectory(directory);
        return directory;
    }
}
