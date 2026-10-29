using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E;

// Shared helpers for the C# browser journeys.
public static class BrowserAudit
{
    public static string WithoutWorkspace(string url)
    {
        var uri = new Uri(url);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        query.Remove("organizationId"); query.Remove("projectId");
        return uri.GetLeftPart(UriPartial.Path) + Microsoft.AspNetCore.Http.QueryString.Create(query) + uri.Fragment;
    }

    // Signs in as a browser does: /login redirects to ts-auth's hosted sign-in. On demo test hosts the
    // hosted page is the test-only /__test/oidc stand-in, told which account to sign in as.
    // Playwright routes never see redirected requests, so the account goes on the landed page's URL.
    public static async Task<IResponse?> SignInAsync(IPage page, Uri origin, string returnTo = "/", string email = "user@example.com", string password = "test-password", bool signUp = false)
    {
        var landed = await page.GotoAsync(new Uri(origin, "/login?returnTo=" + Uri.EscapeDataString(returnTo)).ToString());
        if (new Uri(page.Url).AbsolutePath != "/__test/oidc") return landed;
        var account = "&email=" + Uri.EscapeDataString(email) + "&password=" + Uri.EscapeDataString(password) + (signUp ? "&signup=1" : "");
        var completed = await page.GotoAsync(page.Url + account);
        // Complete the authenticated organization choice as an ordinary browser user does.
        if (new Uri(page.Url).AbsolutePath == "/organizations/choose")
        {
            await page.Locator(".ts-org-picker-option[aria-current=true]").ClickAsync();
            await page.WaitForLoadStateAsync();
        }
        return completed;
    }

    // A protected native form redirects to ts-auth's hosted step-up page. Playwright cannot route a redirected
    // request, so read the challenge from it and approve through the test host as a confirming user would.
    public static async Task SubmitConfirmedAsync(IPage page, Uri origin, Func<Task> submit)
    {
        var hosted = await page.RunAndWaitForRequestAsync(submit, request => new Uri(request.Url).AbsolutePath.StartsWith("/ui/terrascale/step-up/", StringComparison.Ordinal));
        await page.GotoAsync(new Uri(origin, "/__test/step-up/" + new Uri(hosted.Url).Segments.Last() + "?approve=yes").ToString());
    }

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
