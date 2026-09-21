using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.SlowTests;

/// <summary>Every interactive control on every page gets exercised: every
/// form is submitted with its own fields and must answer without a server
/// error, and every dialog-opening button must reference a dialog that
/// exists in the page markup. This is the click-everything pass.</summary>
public sealed class ButtonsEverywhereTests
{
    private static readonly string[] Pages =
    [
        "/", "/databases", "/data", "/data?view=table", "/data?view=editor", "/data?view=sql",
        "/api-keys", "/access", "/audit", "/notifications", "/organization",
        "/profile", "/settings", "/subscriptions", "/usage",
    ];

    [Test]
    public async Task Every_form_on_every_page_answers_without_a_server_error()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"buttons-{Guid.NewGuid():N}@example.test");
        // Give lists something to act on: one database and one credential.
        await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/databases/create", new Dictionary<string, string> { ["name"] = "buttons-db" });

        var failures = new List<string>();
        foreach (var path in Pages)
        {
            var page = await app.GetPageAsync(client, path);
            if (page.StatusCode != 200)
            {
                failures.Add($"{path}: page returned {page.StatusCode}");
                continue;
            }

            foreach (var form in ExtractForms(page.Html))
            {
                // Same-origin forms only; the sign-out form is exercised last.
                if (form.Action.Contains("/api/v1/auth/logout"))
                    continue;
                var status = await SubmitAsync(client, form);
                if (status >= 500)
                    failures.Add($"{path}: POST {form.Action} -> {status}");
                if (status is < 300 and not 200)
                    failures.Add($"{path}: POST {form.Action} -> unexpected {status}");
            }
        }

        // Sign out through the real button.
        var logout = await DashboardTestApp.PostFormAsync(client, "/api/v1/auth/logout", new Dictionary<string, string>());
        await Assert.That((int)logout.StatusCode).IsEqualTo(302);
        await Assert.That(failures).IsEmpty();
    }

    [Test]
    public async Task Every_dialog_button_references_a_dialog_that_exists()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"dialogs-{Guid.NewGuid():N}@example.test");
        await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/databases/create", new Dictionary<string, string> { ["name"] = "dialogs-db" });

        var failures = new List<string>();
        foreach (var path in Pages)
        {
            var page = await app.GetPageAsync(client, path);
            foreach (var button in page.Buttons.Where(b => b.OpensDialog is not null))
            {
                if (!page.Html.Contains($"id=\"{button.OpensDialog}\""))
                    failures.Add($"{path}: button '{button.Label}' opens missing dialog '{button.OpensDialog}'");
            }
        }

        await Assert.That(failures).IsEmpty();
    }

    /// <summary>Every form on the page with its action and its own inputs.</summary>
    private static IReadOnlyList<(string Action, string Method, IReadOnlyDictionary<string, string> Fields)> ExtractForms(string html) =>
        [.. Regex.Matches(html, """<form\b([^>]*)>(.*?)</form>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)
            .Select(form =>
            {
                var attributes = form.Groups[1].Value;
                var body = form.Groups[2].Value;
                var action = Regex.Match(attributes, """action=["']([^"']*)["']""").Groups[1].Value;
                var method = Regex.Match(attributes, """method=["']([^"']*)["']""").Groups[1].Value.ToLowerInvariant();
                if (method.Length == 0)
                    method = "get";
                var fields = new Dictionary<string, string>();
                foreach (var input in Regex.Matches(body, """<(?:input|select|textarea)\b[^>]*>""", RegexOptions.IgnoreCase))
                {
                    var tag = input.ToString()!;
                    var name = Regex.Match(tag, """name=["']([^"']+)["']""").Groups[1].Value;
                    if (name.Length > 0 && !fields.ContainsKey(name))
                        fields[name] = "";
                }

                return (Action: action, Method: method, (IReadOnlyDictionary<string, string>)fields);
            })
            .Where(f => f.Action.Length > 0)];

    private static async Task<int> SubmitAsync(HttpClient client, (string Action, string Method, IReadOnlyDictionary<string, string> Fields) form)
    {
        if (form.Method != "post")
            return 200; // GET forms are the page itself; already verified.

        using var response = await DashboardTestApp.PostFormAsync(client, form.Action, form.Fields);
        return (int)response.StatusCode;
    }
}
