using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.E2E;

[ClassDataSource<DashboardE2EFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("dashboard-demo")]
[Explicit]
[Category("ReferenceCapture")]
public sealed class GoReferenceCaptureJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task OriginalGoRendersAll110ReferenceStatesAndExportsActualWorkspaceIdentities()
    {
        await using var host = await GoReferenceHostScenario.StartAsync(fixture.Root);
        using var playwright = await Playwright.CreateAsync();
        // Fixed Chromium layout-test raster path, shared with the C# comparison.
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        { Headless = true, Args = ["--disable-gpu", "--disable-skia-runtime-opts", "--disable-partial-raster"] });
        var output = Path.Combine(fixture.Root, "test-results", "parity");
        Directory.CreateDirectory(output);
        var stage = Path.Combine(output, "capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var snapshot = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "tests", "parity", "snapshot.js"));
        var screenshots = new List<object>();
        var states = new HashSet<string>(StringComparer.Ordinal);
        var pageErrors = new ConcurrentQueue<string>();
        var routeEmail = "e2e-migration-reference-" + Guid.NewGuid().ToString("N")[..12] + "@example.test";
        const string routePassword = "E2e-journey-123!";
        await using var routes = await browser.NewContextAsync(new() { BaseURL = host.Origin.ToString() });
        var registration = await NewPageAsync(routes);
        await NavigateAsync(registration, "/onboarding");
        var form = registration.Locator("form[action='/onboarding']");
        await form.Locator("[name=displayName]").FillAsync("E2E migration-reference");
        await form.Locator("[name=email]").FillAsync(routeEmail);
        await form.Locator("[name=password]").FillAsync(routePassword);
        await form.Locator("[name=confirmPassword]").FillAsync(routePassword);
        await form.Locator("[name=terms]").CheckAsync();
        await form.GetByRole(AriaRole.Button, new() { Name = "Create account" }).ClickAsync();
        var payment = registration.Locator("form[action='/onboarding/step']").Filter(new()
        { Has = registration.Locator("[name=step][value=payment-method]") });
        await payment.WaitForAsync();
        await payment.GetByRole(AriaRole.Button, new() { Name = "Continue on Free", Exact = true }).ClickAsync();
        var project = registration.Locator("form[action='/onboarding/step']").Filter(new()
        { Has = registration.Locator("[name=step][value=project]") });
        await project.Locator("[name=name]").FillAsync("Personal");
        await project.Locator("[name=region]").SelectOptionAsync("GRU");
        await project.GetByRole(AriaRole.Button, new() { Name = "Create project" }).ClickAsync();
        await registration.WaitForURLAsync(host.Origin.ToString());

        // Read the inventory from the original Go source so changing its
        // route list cannot silently leave a different reference inventory.
        var routeSource = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "tests", "e2e", "pages_test.go"));
        var inventory = Regex.Match(routeSource, @"var parityRouteStates = \[\]routeState\{(?<states>[\s\S]*?)\n\}",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Groups["states"].Value;
        var routeStates = Regex.Matches(inventory, "\\{key: \"([^\"]+)\", path: \"([^\"]+)\"([^\n]*)\\}",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Cast<Match>()
            .Where(match => !match.Groups[3].Value.Contains("anonymous: true", StringComparison.Ordinal) && match.Groups[1].Value != "reset-password").ToArray();
        await Assert.That(routeStates.Length).IsEqualTo(36);
        foreach (var route in routeStates)
        {
            var page = await NewPageAsync(routes);
            var path = route.Groups[2].Value;
            var response = await NavigateAsync(page, path);
            await CaptureAsync(page, "route-" + route.Groups[1].Value, path, "signed-in", response.Status);
            await page.CloseAsync();
        }

        await using var seeded = await browser.NewContextAsync(new() { BaseURL = host.Origin.ToString() });
        var seededPage = await NewPageAsync(seeded);
        await NavigateAsync(seededPage, "/login");
        await seededPage.Locator("#email").FillAsync("mario@terrascale.tech");
        await seededPage.Locator("#password").FillAsync("terrascale");
        await seededPage.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await seededPage.WaitForURLAsync(host.Origin.ToString());
        foreach (var (key, path) in new (string, string)[]
        {
            ("database", "/databases/0Z793APJ2ECFW2F7P6J9F2RKVY"),
            ("documents", "/databases/0Z793APJ2ECFW2F7P6J9F2RKVY/documents"),
            ("sql", "/databases/0Z793APJ2ECFW2F7P6J9F2RKVY/sql"),
            ("backups", "/databases/0Z793APJ2ECFW2F7P6J9F2RKVY/backups"),
            ("projects", "/organization?tab=projects"), ("billing", "/organization?tab=billing"),
            ("invitations", "/organization?tab=invitations"), ("admin", "/admin"), ("onboarding", "/onboarding")
        })
        {
            var response = await NavigateAsync(seededPage, path);
            await CaptureAsync(seededPage, "seeded-" + key, path, "signed-in", response.Status);
        }

        await using var anonymous = await browser.NewContextAsync(new() { BaseURL = host.NativeOrigin.ToString() });
        var native = await NewPageAsync(anonymous);
        foreach (var (key, path) in new (string, string)[]
        {
            ("login", "/login"), ("code", "/login/code"), ("reset", "/reset-password"), ("notfound", "/missing"),
            ("login-return", "/login?returnTo=%2Fdatabases"), ("code-prefill", "/login/code?email=user%40example.com&returnTo=%2Fdatabases")
        })
        {
            var response = await NavigateAsync(native, path);
            await CaptureAsync(native, key, path, "anonymous", response.Status);
        }
        await NavigateAsync(native, "/login/code");
        await native.Locator("#email").FillAsync("user@example.com");
        await native.GetByRole(AriaRole.Button, new() { Name = "Email me a code", Exact = true }).ClickAsync();
        await native.GetByRole(AriaRole.Heading, new() { Name = "Check your email", Exact = true }).WaitForAsync();
        await native.Locator(".ts-email-code-ready").WaitForAsync();
        await CaptureAsync(native, "code-verify", "/login/code", "code-verify", 200);
        await native.Locator("#code").FillAsync("123456");
        await Microsoft.Playwright.Assertions.Expect(native.Locator(".ts-email-code-slot").Nth(5)).ToHaveTextAsync("6");
        await CaptureAsync(native, "code-filled", "/login/code", "code-filled", 200);
        await NavigateAsync(native, "/reset-password");
        await native.Locator("#email").FillAsync("user@example.com");
        await native.GetByRole(AriaRole.Button, new() { Name = "Email me a code", Exact = true }).ClickAsync();
        await native.GetByRole(AriaRole.Heading, new() { Name = "Choose a new password", Exact = true }).WaitForAsync();
        await CaptureAsync(native, "reset-complete", "/reset-password", "reset-complete", 200);
        await NavigateAsync(native, "/login");
        await native.Locator("#email").FillAsync("user@example.com");
        await native.Locator("#password").FillAsync("wrong-password");
        var rejection = await native.RunAndWaitForResponseAsync(
            () => native.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync(),
            response => response.Request.IsNavigationRequest && response.Request.Method == "POST");
        await native.GetByRole(AriaRole.Alert).WaitForAsync();
        await Assert.That(rejection.Status).IsEqualTo(401);
        await CaptureAsync(native, "login-rejected", "/login", "login-rejected", rejection.Status);
        await Assert.That(states.Count).IsEqualTo(55);
        await Assert.That(screenshots.Count).IsEqualTo(110);
        await Assert.That(pageErrors.Count).IsEqualTo(0).Because(string.Join('\n', pageErrors));

        // Capture through authenticated application HTTP only, after every
        // screenshot, so minting the short-lived PAT cannot affect a page.
        var routeManifest = await ManifestAsync(host, routes, routePassword, includeInventory: true);
        var seededManifest = await ManifestAsync(host, seeded, "terrascale", includeInventory: false);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var reference = JsonSerializer.Serialize(screenshots, options) + "\n";
        await File.WriteAllTextAsync(Path.Combine(stage, "reference.json"), reference);
        var manifest = new
        {
            version = 1,
            capturedAt = DateTimeOffset.UtcNow,
            states = states.Count,
            screenshots = screenshots.Count,
            source = new { commit = await CommitAsync(fixture.Root), renderingSha256 = GoReferenceProof.RenderingDigest(fixture.Root), captureSha256 = GoReferenceProof.CaptureDigest(fixture.Root), snapshotSha256 = Hash(snapshot) },
            referenceSha256 = Hash(reference),
            routes = routeManifest.Persona,
            seeded = seededManifest.Persona,
            mcp = routeManifest.Inventory
        };
        await File.WriteAllTextAsync(Path.Combine(stage, "go-fixture-manifest.json"), JsonSerializer.Serialize(manifest, options) + "\n");
        // Publishing is a separate explicit capture operation. Comparison
        // journeys never regenerate references or replace their strict rules.
        foreach (var path in Directory.GetFiles(stage)) File.Move(path, Path.Combine(output, Path.GetFileName(path)), overwrite: true);
        Directory.Delete(stage);
        Console.WriteLine("Captured 110 original Go desktop/mobile references and authenticated identity/workspace manifest.");

        async Task<IPage> NewPageAsync(IBrowserContext context)
        {
            var page = await context.NewPageAsync();
            page.PageError += (_, error) => pageErrors.Enqueue(error);
            return page;
        }

        async Task CaptureAsync(IPage page, string key, string path, string setup, int status)
        {
            await Assert.That(states.Add(key)).IsTrue().Because("Each original Go state is captured exactly once at both sizes: " + key);
            foreach (var (name, width, height) in new[] { ("desktop", 1440, 900), ("mobile", 390, 844) })
            {
                await page.SetViewportSizeAsync(width, height);
                // Hidden dialog editors mount too and add form controls.
                // Capture every editor after the same real upgrade boundary.
                await page.WaitForFunctionAsync("() => [...document.querySelectorAll('ts-code-editor')].every(editor => editor.hasAttribute('data-ready'))");
                if (key == "seeded-sql")
                {
                    await page.Locator("#sql-editor ts-code-editor[data-ready]").WaitForAsync(new() { State = WaitForSelectorState.Attached });
                    await page.GetByRole(AriaRole.Button, new() { Name = "Format", Exact = true }).WaitForAsync();
                }
                await page.EvaluateAsync("() => document.fonts.ready.then(() => true)");
                await page.EvaluateAsync("async () => { await Promise.all([...document.images].filter(img => img.getClientRects().length && img.getBoundingClientRect().top < innerHeight).map(img => img.decode().catch(() => {}))); }");
                await page.EvaluateAsync("""
                    async () => {
                        const freeze = async () => { for (const animation of document.getAnimations()) { await animation.ready; if (animation.effect.getComputedTiming().iterations === Infinity) { animation.pause(); await animation.ready; animation.currentTime = 0; } else { animation.finish(); } } };
                        await freeze();
                        await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
                        await freeze();
                        await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
                    }
                    """);
                var image = key + "-" + name + "-go.png";
                await File.WriteAllBytesAsync(Path.Combine(stage, image), await StableScreenshotAsync(page));
                // Transfer the original browser value as JSON text. Playwright's
                // object transport includes reference IDs that are not DOM data.
                var snapshotJson = await page.EvaluateAsync<string>("() => JSON.stringify((" + snapshot + ")())");
                using var document = JsonDocument.Parse(snapshotJson);
                var actual = document.RootElement.Clone();
                screenshots.Add(new { key, path, setup, status, viewport = new { width, height }, size = name, screenshot = image, snapshot = actual });
            }
        }
    }

    private static async Task<byte[]> StableScreenshotAsync(IPage page)
    {
        // Compare consecutive frames of this host only, never retry until a
        // different host's reference matches. Pixel comparison stays exact.
        byte[]? previous = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var current = await page.ScreenshotAsync(new() { FullPage = true, Caret = ScreenshotCaret.Hide });
            if (previous is not null && previous.AsSpan().SequenceEqual(current)) return current;
            previous = current;
        }
        throw new IOException("The original Go page did not produce two identical consecutive screenshots: " + page.Url);
    }

    private static async Task<IResponse> NavigateAsync(IPage page, string path)
        => await page.GotoAsync(path) ?? throw new IOException("The original Go page returned no document response: " + path);

    private static async Task<(object Persona, object? Inventory)> ManifestAsync(GoReferenceHostScenario host,
        IBrowserContext context, string password, bool includeInventory)
    {
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", (await context.CookiesAsync()).Select(cookie => cookie.Name + "=" + cookie.Value)));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        var csrf = await HttpJourney.CsrfAsync(client, "/profile?tab=access-tokens");
        string tokenId; string secret;
        using (var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/profile/access-tokens", new()
        { ["_csrf"] = csrf, ["name"] = "TUnit reference manifest", ["password"] = password, ["expiresInDays"] = "7" }))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            secret = document.RootElement.GetProperty("secret").GetString()!;
            tokenId = document.RootElement.GetProperty("token").GetProperty("id").GetString()!;
        }
        try
        {
            var discovered = await GoMcpAsync(client, secret, "server/discover", new { });
            await Assert.That(discovered.GetProperty("result").GetProperty("supportedVersions").EnumerateArray()
                .Any(version => version.GetString() == "2026-07-28")).IsTrue();
            var identity = await ToolAsync("get_profile", new { });
            var workspace = await ToolAsync("get_context", new { });
            await Assert.That(TerraScaleIds.IsValid(identity.GetProperty("userId").GetString()!)).IsTrue();
            await Assert.That(TerraScaleIds.IsValid(identity.GetProperty("accountId").GetString()!)).IsTrue();
            var organizations = new List<JsonElement>();
            var memberships = new List<object>();
            var invitations = new List<object>();
            var databaseInventories = new List<object>();
            var databaseDetails = new List<object>();
            var organizationIds = new HashSet<string>(StringComparer.Ordinal);
            var projectIds = new HashSet<string>(StringComparer.Ordinal);
            var databaseIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var organization in workspace.GetProperty("organizations").EnumerateArray())
            {
                var id = organization.GetProperty("id").GetString()!;
                await Assert.That(TerraScaleIds.IsValid(id) && organizationIds.Add(id)).IsTrue();
                var detail = await ToolAsync("get_organization", new { organizationId = id });
                await Assert.That(detail.GetProperty("id").GetString()).IsEqualTo(id);
                organizations.Add(detail);
                var members = await ToolAsync("list_members", new { organizationId = id });
                var rows = members.GetProperty("members").EnumerateArray().ToArray();
                await Assert.That(rows.Length).IsEqualTo(detail.GetProperty("memberCount").GetInt32());
                var memberIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var member in rows)
                {
                    var memberId = member.GetProperty("id").GetString()!;
                    await Assert.That(TerraScaleIds.IsValid(memberId) && memberIds.Add(memberId)).IsTrue()
                        .Because("The source member inventory must have distinct canonical identities.");
                    await Assert.That(DateOnly.TryParseExact(member.GetProperty("joinedAt").GetString(), "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)).IsTrue()
                        .Because("The source exposes member join dates with day precision only.");
                }
                var self = rows.Single(member => member.GetProperty("isSelf").GetBoolean());
                foreach (var field in new[] { "name", "email" })
                    await Assert.That(self.GetProperty(field).GetString()).IsEqualTo(identity.GetProperty(field).GetString());
                await Assert.That(self.GetProperty("id").GetString()).IsEqualTo(identity.GetProperty("userId").GetString());
                // Keep the exact structured response and its organization scope;
                // timestamps are not reconstructed from organization metadata.
                memberships.Add(new { organizationId = id, result = members });
                var invitationResult = await ToolAsync("list_invitations", new { organizationId = id });
                var invitationIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var invitation in Rows(invitationResult, "invitations"))
                {
                    var invitationId = invitation.GetProperty("id").GetString()!;
                    await Assert.That(TerraScaleIds.IsValid(invitationId) && invitationIds.Add(invitationId)).IsTrue();
                    await Assert.That(invitation.GetProperty("status").GetString() is "Pending" or "Accepted" or "Declined" or "Canceled" or "Expired").IsTrue();
                    var expiry = invitation.GetProperty("expiresAt").GetString();
                    await Assert.That(expiry == "" || DateTimeOffset.TryParse(expiry,
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out _)).IsTrue();
                    await Assert.That(invitation.GetProperty("open").ValueKind is JsonValueKind.True or JsonValueKind.False).IsTrue();
                }
                invitations.Add(new { organizationId = id, result = invitationResult });

                foreach (var project in organization.GetProperty("projects").EnumerateArray())
                {
                    var projectId = project.GetProperty("id").GetString()!;
                    await Assert.That(TerraScaleIds.IsValid(projectId) && projectIds.Add(projectId)).IsTrue();
                    var databaseResult = await ToolAsync("list_databases", new { organizationId = id, projectId });
                    await Assert.That(Rows(databaseResult, "databases").Count()).IsEqualTo(project.GetProperty("databases").GetInt32())
                        .Because("The source project counts and scoped database inventory must describe the same fixture.");
                    databaseInventories.Add(new { organizationId = id, projectId, result = databaseResult });
                    foreach (var database in Rows(databaseResult, "databases"))
                    {
                        var databaseId = database.GetProperty("id").GetString()!;
                        await Assert.That(TerraScaleIds.IsValid(databaseId) && databaseIds.Add(databaseId)).IsTrue();
                        var databaseDetail = await ToolAsync("get_database", new { organizationId = id, projectId, databaseId });
                        foreach (var field in new[] { "id", "name", "status", "region", "createdAt", "placement" })
                            await Assert.That(databaseDetail.GetProperty(field).GetRawText()).IsEqualTo(database.GetProperty(field).GetRawText())
                                .Because("The source database inventory and detail must agree in the same scope.");
                        foreach (var field in new[] { "collections", "documents", "bytes" })
                            await Assert.That(databaseDetail.GetProperty(field).GetInt64() >= 0).IsTrue();
                        var backups = await ToolAsync("list_backups", new { organizationId = id, projectId, databaseId });
                        var backupIds = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var backup in Rows(backups, "backups"))
                        {
                            await Assert.That(backupIds.Add(backup.GetProperty("id").GetString()!)).IsTrue();
                            await Assert.That(backup.GetProperty("sizeBytes").GetInt64() >= 0).IsTrue();
                        }
                        // Preserve actual provider results, including omitted policy
                        // fields and unavailable counts. This is read-only test data,
                        // never a production fallback or authority grant.
                        databaseDetails.Add(new { organizationId = id, projectId, databaseId, detail = databaseDetail, backups });
                    }
                }
            }
            object? inventory = includeInventory ? new
            {
                protocol = "2026-07-28",
                principalUserId = identity.GetProperty("userId").GetString(),
                discover = discovered,
                toolsList = await ListAsync("tools/list", "tools", true),
                resourcesList = await ListAsync("resources/list", "resources", false),
                resourceTemplatesList = await ListAsync("resources/templates/list", "resourceTemplates", false)
            } : null;
            return (new
            {
                identity,
                context = workspace,
                organizations,
                members = memberships,
                invitations,
                databaseInventories,
                databaseDetails,
                password
            }, inventory);

            static IEnumerable<JsonElement> Rows(JsonElement result, string field)
            {
                var rows = result.GetProperty(field);
                if (rows.ValueKind == JsonValueKind.Null) return [];
                if (rows.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("The source inventory is not an array: " + field);
                return rows.EnumerateArray().ToArray();
            }

            async Task<JsonElement> ToolAsync(string name, object arguments)
            {
                var rpc = await GoMcpAsync(client, secret, "tools/call", new { name, arguments });
                var result = rpc.GetProperty("result");
                await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse()
                    .Because("Actual Go manifest read failed: " + name);
                return result.GetProperty("structuredContent").Clone();
            }

            async Task<IReadOnlyList<JsonElement>> ListAsync(string method, string collection, bool required)
            {
                var pages = new List<JsonElement>();
                var cursors = new HashSet<string>(StringComparer.Ordinal);
                string? cursor = null;
                do
                {
                    var arguments = new Dictionary<string, object?>();
                    if (cursor is not null) arguments["cursor"] = cursor;
                    var rpc = await GoMcpAsync(client, secret, method, arguments);
                    pages.Add(rpc);
                    if (rpc.TryGetProperty("error", out _))
                    {
                        await Assert.That(required).IsFalse().Because("Original Go MCP inventory: " + method);
                        break;
                    }
                    var result = rpc.GetProperty("result");
                    await Assert.That(result.GetProperty(collection).ValueKind).IsEqualTo(JsonValueKind.Array);
                    cursor = result.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
                    if (!string.IsNullOrEmpty(cursor)) await Assert.That(cursors.Add(cursor)).IsTrue().Because("The source inventory cannot repeat a pagination cursor.");
                } while (!string.IsNullOrEmpty(cursor));
                return pages;
            }
        }
        finally
        {
            using var revoked = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/profile/access-tokens/" + tokenId + "/revoke", new() { ["_csrf"] = csrf });
            await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static async Task<JsonElement> GoMcpAsync(HttpClient client, string secret, string method, object arguments)
    {
        const string protocol = "2026-07-28";
        var parameters = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(arguments))!;
        parameters["_meta"] = new Dictionary<string, object?>
        {
            ["io.modelcontextprotocol/protocolVersion"] = protocol,
            ["io.modelcontextprotocol/clientCapabilities"] = new { },
            ["io.modelcontextprotocol/clientInfo"] = new { name = "TUnit Go reference capture", version = "1" }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters }), Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
        request.Headers.Add("MCP-Protocol-Version", protocol);
        request.Headers.Add("Mcp-Method", method);
        if (method == "tools/call")
            request.Headers.Add("Mcp-Name", ((JsonElement)parameters["name"]!).GetString()!);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            body = string.Join('\n', body.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal)).Select(line => line[5..].TrimStart()));
        using var document = JsonDocument.Parse(body);
        var unsupportedOptionalInventory = method is "resources/list" or "resources/templates/list"
            && response.StatusCode == HttpStatusCode.NotFound
            && document.RootElement.TryGetProperty("error", out var error)
            && error.GetProperty("code").GetInt32() == -32601;
        await Assert.That(response.StatusCode == HttpStatusCode.OK || unsupportedOptionalInventory).IsTrue()
            .Because("Original Go MCP " + method + ": " + body);
        return document.RootElement.Clone();
    }

    private static async Task<string> CommitAsync(string root)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("rev-parse"); start.ArgumentList.Add("HEAD");
        using var process = Process.Start(start) ?? throw new IOException("Could not read the original Go source provenance.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException(await error);
        return (await output).Trim();
    }
}
