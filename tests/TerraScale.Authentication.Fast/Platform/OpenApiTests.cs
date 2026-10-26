using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Platform;

public sealed partial class OpenApiTests
{
    [ClassDataSource<BrowserFixture>(Shared = SharedType.PerTestSession)]
    public required BrowserFixture Chromium { get; init; }

    private static readonly string[] ExpectedSchemes = ["ManagementBearer", "OAuthAccessBearer", "OperatorBearer", "SessionCookie", "accountDeletionReceipt", "apiKeyToken", "bearerAuth", "clientBasic", "mailAdmission", "platformUser", "projectApiKey"];
    private static readonly string[] Methods = ["get", "put", "post", "delete", "patch", "head", "options", "trace"];

    [Test]
    public async Task The_contract_is_served_and_complete()
    {
        await using var server = await AuthTestServer.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/openapi/v1.json");
        request.Headers.Host = "attacker.example";
        using var response = await server.Client.SendAsync(request);
        var document = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var paths = document.GetProperty("paths");
        var export = paths.GetProperty("/v1/{project}/users/export").GetProperty("get");
        await Assert.That(export.GetProperty("parameters").EnumerateArray().Where(parameter => parameter.GetProperty("in").GetString() == "query")
            .Select(parameter => parameter.GetProperty("name").GetString()!).ToList()).IsEquivalentTo(["limit", "after", "format"]);
        var exported = export.GetProperty("responses").GetProperty("200");
        await Assert.That(exported.GetProperty("content").TryGetProperty("text/csv", out _)).IsTrue();
        await Assert.That(exported.GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString()).IsEqualTo("#/components/schemas/DirectoryPageBody");
        await Assert.That(exported.GetProperty("headers").TryGetProperty("X-Export-Next", out _)).IsTrue();

        var schemas = document.GetProperty("components").GetProperty("schemas");
        await Assert.That(schemas.GetProperty("DirectoryPageBody").GetProperty("properties").EnumerateObject().Select(field => field.Name).ToList())
            .IsEquivalentTo(["items", "next", "created_before"]);
        await Assert.That(schemas.GetProperty("DirectoryUserBody").GetProperty("properties").EnumerateObject().Select(field => field.Name).ToList())
            .IsEquivalentTo(["id", "email", "email_verified", "created_at", "disabled", "linked_providers"]);
        await Assert.That(schemas.GetProperty("DirectoryProviderBody").GetProperty("properties").EnumerateObject().Select(field => field.Name).ToList())
            .IsEquivalentTo(["provider", "client_id", "subject"]);
        await Assert.That(schemas.GetProperty("BrandingBody").GetProperty("properties").TryGetProperty("email_message", out _)).IsTrue();

        var userInfo = paths.GetProperty("/{slug}/oauth/userinfo").GetProperty("get");
        await Assert.That(userInfo.GetProperty("responses").GetProperty("503").GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("$ref").GetString()).IsEqualTo("#/components/schemas/OAuthErrorBody");
        await Assert.That(document.GetProperty("components").GetProperty("securitySchemes").GetProperty("platformUser")
            .GetProperty("description").GetString()).Contains("active MFA");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(document.GetProperty("openapi").GetString()).StartsWith("3.");
        await Assert.That(document.GetProperty("info").GetProperty("title").GetString()).IsEqualTo("TerraScale Auth API");
        await Assert.That(paths.TryGetProperty("/healthz", out _)).IsTrue();
        await Assert.That(paths.TryGetProperty("/readyz", out _)).IsTrue();
        await Assert.That(paths.TryGetProperty("/admin/{asset}", out _)).IsTrue();
        foreach (var oidcPath in new[] { "/{slug}/.well-known/openid-configuration", "/{slug}/.well-known/jwks.json", "/{slug}/oauth/authorize", "/{slug}/oauth/token", "/{slug}/oauth/userinfo", "/{slug}/oauth/logout", "/ui/{slug}/sign-in", "/ui/{slug}/sign-in/verify", "/{slug}/oauth/revoke" })
        {
            await Assert.That(paths.TryGetProperty(oidcPath, out _)).IsTrue();
        }

        // /metrics is served only on the metrics listener, never in the public API.
        await Assert.That(paths.TryGetProperty("/metrics", out _)).IsFalse();

        // Probes and bundled assets are public and say so explicitly.
        var healthz = paths.GetProperty("/healthz").GetProperty("get");
        await Assert.That(healthz.GetProperty("security").EnumerateArray().Single().EnumerateObject().Any()).IsFalse();
        // The server is the configured public origin, not whatever Host header the request sent.
        await Assert.That(document.GetProperty("servers")[0].GetProperty("url").GetString()).IsEqualTo("https://auth.test.invalid");

        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths.EnumerateObject())
        {
            foreach (var method in Methods)
            {
                if (!path.Value.TryGetProperty(method, out var operation))
                {
                    continue;
                }

                await Assert.That(operation.GetProperty("responses").EnumerateObject().Any()).IsTrue();
                if (path.Name is "/{slug}/oauth/token" or "/ui/{slug}/sign-in" or "/ui/{slug}/sign-in/verify"
                    && method == "post")
                {
                    var retry = operation.GetProperty("responses").GetProperty("429").GetProperty("headers").GetProperty("Retry-After");
                    await Assert.That(retry.GetProperty("required").GetBoolean()).IsTrue();
                    await Assert.That(retry.GetProperty("schema").GetProperty("minimum").GetInt32()).IsEqualTo(1);
                }
                if (operation.TryGetProperty("operationId", out var id))
                {
                    await Assert.That(operationIds.Add(id.GetString()!)).IsTrue();
                }
            }
        }
    }

    [Test]
    public async Task Operations_declare_the_credential_their_route_requires()
    {
        // Test-only routes stand in for the feature endpoints that later streams add. They
        // carry a described tag: an undescribed one fails document generation by design.
        await using var server = await AuthTestServer.StartAsync(routes: app =>
        {
            app.MapGet("/v1/{slug}/__test/users", () => Results.Ok()).WithName("test_management").WithTags("Admin");
            app.MapGet("/ui/{slug}/admin/__test", () => Results.Ok()).WithName("test_admin").WithTags("Admin");
            app.MapPost("/v1/instances", () => Results.Ok()).WithName("test_instances").WithTags("Admin");
            app.MapGet("/v1/{slug}/__test/public", () => Results.Ok()).WithName("test_public").WithTags("Admin").AllowAnonymous();
            var group = app.MapGroup("/scim/{directory}/__group").WithMetadata(new TerraScale.Authentication.Hosting.RequiresSecurity("bearerAuth"));
            group.MapGet("/open", () => Results.Ok()).WithName("test_group_open").WithTags("Admin").AllowAnonymous();
            group.MapGet("/operator", () => Results.Ok()).WithName("test_group_operator").WithTags("Admin")
                .WithMetadata(new TerraScale.Authentication.Hosting.RequiresSecurity("OperatorBearer"));
            app.MapPost("/scim/{directory}/__test", () => Results.Ok()).WithName("test_scim").WithTags("Admin")
                .WithMetadata(new TerraScale.Authentication.Hosting.RequiresSecurity("bearerAuth"));
        });

        using var response = await server.Client.GetAsync("/openapi/v1.json");
        var document = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var paths = document.GetProperty("paths");
        static string Scheme(JsonElement operation) =>
            operation.GetProperty("security").EnumerateArray().Single().EnumerateObject().Single().Name;

        await Assert.That(Scheme(paths.GetProperty("/v1/{slug}/__test/users").GetProperty("get"))).IsEqualTo("ManagementBearer");
        await Assert.That(Scheme(paths.GetProperty("/ui/{slug}/admin/__test").GetProperty("get"))).IsEqualTo("SessionCookie");
        await Assert.That(Scheme(paths.GetProperty("/{slug}/oauth/userinfo").GetProperty("get"))).IsEqualTo("OAuthAccessBearer");
        foreach (var path in new[] { "/{slug}/oauth/token", "/{slug}/oauth/revoke" })
        {
            var operation = paths.GetProperty(path).GetProperty("post");
            var alternatives = operation.GetProperty("security").EnumerateArray().Select(requirement =>
                string.Join(",", requirement.EnumerateObject().Select(scheme => scheme.Name))).ToArray();
            await Assert.That(alternatives).IsEquivalentTo(["clientBasic", string.Empty]);
            await Assert.That(operation.GetProperty("description").GetString()).Contains("client_secret_post");
            await Assert.That(operation.GetProperty("description").GetString()).Contains("Public clients send client_id");
        }

        await Assert.That(paths.TryGetProperty("/{slug}/oauth/device", out _)).IsTrue();
        await Assert.That(paths.TryGetProperty("/{slug}/oauth/device_authorization", out _)).IsFalse();
        await Assert.That(Scheme(paths.GetProperty("/v1/instances").GetProperty("post"))).IsEqualTo("OperatorBearer");
        await Assert.That(Scheme(paths.GetProperty("/scim/{directory}/__test").GetProperty("post"))).IsEqualTo("bearerAuth");
        // An endpoint's own declaration beats its group's; AllowAnonymous beats both.
        var groupOpen = paths.GetProperty("/scim/{directory}/__group/open").GetProperty("get").GetProperty("security");
        await Assert.That(groupOpen.EnumerateArray().Single().EnumerateObject().Any()).IsFalse();
        await Assert.That(Scheme(paths.GetProperty("/scim/{directory}/__group/operator").GetProperty("get"))).IsEqualTo("OperatorBearer");
        // An explicitly anonymous endpoint under a protected prefix stays anonymous.
        var publicSecurity = paths.GetProperty("/v1/{slug}/__test/public").GetProperty("get").GetProperty("security");
        await Assert.That(publicSecurity.EnumerateArray().Single().EnumerateObject().Any()).IsFalse();

        // Exactly the referenced schemes are defined, each with its real mechanism.
        var schemes = document.GetProperty("components").GetProperty("securitySchemes");
        await Assert.That(schemes.EnumerateObject().Select(scheme => scheme.Name).Order().ToArray())
            .IsEquivalentTo(ExpectedSchemes);
        await Assert.That(schemes.GetProperty("SessionCookie").GetProperty("in").GetString()).IsEqualTo("cookie");
        await Assert.That(schemes.GetProperty("SessionCookie").GetProperty("name").GetString()).IsEqualTo("tsauth_session");
        await Assert.That(schemes.GetProperty("ManagementBearer").GetProperty("scheme").GetString()).IsEqualTo("bearer");
        await Assert.That(schemes.GetProperty("clientBasic").GetProperty("scheme").GetString()).IsEqualTo("basic");
        await Assert.That(Scheme(paths.GetProperty("/v1/terrascale/email/admission").GetProperty("post"))).IsEqualTo("mailAdmission");
        await Assert.That(schemes.GetProperty("mailAdmission").GetProperty("scheme").GetString()).IsEqualTo("bearer");
        await Assert.That(Scheme(paths.GetProperty("/v1/terrascale/account/deletion").GetProperty("get"))).IsEqualTo("accountDeletionReceipt");
        await Assert.That(schemes.GetProperty("accountDeletionReceipt").GetProperty("in").GetString()).IsEqualTo("header");
        await Assert.That(schemes.GetProperty("accountDeletionReceipt").GetProperty("name").GetString()).IsEqualTo("X-TerraScale-Deletion-Receipt");
    }

    [Test]
    public async Task Served_platform_operations_match_the_published_contract()
    {
        // The platform contract and its supplements must cover every served platform operation,
        // including its exact security alternatives, response codes, path, method and operationId.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TerraScale.slnx")))
        {
            root = root.Parent;
        }

        List<string> contractFiles = ["account-providers.openapi.yaml", "email-admission.openapi.yaml"];

        // The YAML is read line by line in the layout this repository writes; a line this cannot place fails the test
        // rather than being skipped, so no published operation goes unchecked.
        var contract = new Dictionary<(string Path, string Method, string Id), SortedSet<string>>();
        var security = new Dictionary<(string Path, string Method, string Id), SortedSet<string>>();
        var operationIds = 0;
        var planned = new HashSet<(string Path, string Method, string Id)>();
        foreach (var file in new[] { "platform-api.openapi.yaml", "scoped-end-user-tokens.openapi.yaml", "account-deletion.openapi.yaml", "deletion-history.openapi.yaml" })
        {
            contractFiles.Add(file);
        }

        foreach (var file in contractFiles)
        {
            string? path = null, method = null;
            SortedSet<string>? statuses = null;
            SortedSet<string> defaultSecurity = [];
            SortedSet<string>? securityTarget = null;
            var securityIndent = string.Empty;
            (string Path, string Method, string Id)? current = null;
            foreach (var line in await File.ReadAllLinesAsync(Path.Combine(root!.FullName, "docs", "auth", file)))
            {
                if (securityTarget is not null && line.StartsWith(securityIndent, StringComparison.Ordinal))
                {
                    var requirement = ContractSecurity().Match(line);
                    if (!requirement.Success || !securityTarget.Add(requirement.Groups["scheme"].Value))
                    {
                        throw new InvalidOperationException($"Unsupported or duplicate security alternative in {file}: {line}");
                    }

                    continue;
                }

                securityTarget = null;
                if (line == "security:")
                {
                    securityTarget = defaultSecurity;
                    securityIndent = "  ";
                    continue;
                }

                operationIds += line.TrimStart().StartsWith("operationId:", StringComparison.Ordinal) ? 1 : 0;
                if (ContractPath().Match(line) is { Success: true } pathMatch)
                {
                    path = pathMatch.Groups["path"].Value;
                    method = null;
                    statuses = null;
                }
                else if (ContractMethod().Match(line) is { Success: true } methodMatch)
                {
                    method = methodMatch.Groups["method"].Value;
                    statuses = null;
                }
                else if (ContractPathMember().IsMatch(line))
                {
                    // A path's parameters, summary or description: not an operation.
                    method = null;
                    statuses = null;
                }
                else if (ContractOperation().Match(line) is { Success: true } operation && path is not null && method is not null)
                {
                    statuses = [];
                    current = (path, method, operation.Groups["id"].Value);
                    contract.Add(current.Value, statuses);
                    security.Add(current.Value, [.. defaultSecurity]);
                }
                else if ((line is "      security:" or "      security: [{}]") && current is { } secured)
                {
                    security[secured].Clear();
                    if (line.EndsWith("[{}]", StringComparison.Ordinal))
                    {
                        security[secured].Add(string.Empty);
                    }
                    else
                    {
                        securityTarget = security[secured];
                        securityIndent = "        ";
                    }
                }
                else if (line.StartsWith("      security:", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Unsupported security declaration in {file}: {line}");
                }
                else if (line == "      x-planned: true" && current is { } marked)
                {
                    // Published ahead of its slice: documented, not served yet.
                    planned.Add(marked);
                }
                else if (ContractResponse().Match(line) is { Success: true } documentedStatus)
                {
                    statuses?.Add(documentedStatus.Groups["status"].Value);
                }
            }
        }

        await using var server = await AuthTestServer.StartAsync();
        using var response = await server.Client.GetAsync("/openapi/v1.json");
        var paths = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync()).GetProperty("paths");
        var served = new HashSet<(string Path, string Method, string Id)>();
        List<string> mismatches = [];
        foreach (var item in paths.EnumerateObject().Where(item => item.Name.StartsWith("/v1/terrascale/", StringComparison.Ordinal)))
        {
            foreach (var verb in Methods.Where(verb => item.Value.TryGetProperty(verb, out _)))
            {
                var operation = item.Value.GetProperty(verb);
                var id = operation.GetProperty("operationId").GetString()!;
                await Assert.That(contract.TryGetValue((item.Name, verb, id), out var documented)).IsTrue();
                var answered = string.Join(',', operation.GetProperty("responses").EnumerateObject().Select(status => status.Name).Order(StringComparer.Ordinal));
                if (answered != string.Join(',', documented!))
                {
                    mismatches.Add($"{id}: served {answered}, contract {string.Join(',', documented!)}");
                }

                var declaredSecurity = security[(item.Name, verb, id)];
                await Assert.That(declaredSecurity.Count).IsGreaterThan(0);
                var servedSecurity = operation.GetProperty("security").EnumerateArray().Select(requirement =>
                {
                    var schemes = requirement.EnumerateObject().ToArray();
                    // These contracts use alternatives with one scheme and no OAuth scopes.
                    if (schemes.Length > 1 || schemes.Any(scheme => scheme.Value.GetArrayLength() != 0))
                    {
                        throw new InvalidOperationException($"Unsupported served security requirement for {id}");
                    }

                    return schemes.Length == 0 ? string.Empty : schemes[0].Name;
                }).Order(StringComparer.Ordinal).ToArray();
                await Assert.That(servedSecurity).IsEquivalentTo(declaredSecurity.ToArray());
                served.Add((item.Name, verb, id));
            }
        }

        await Assert.That(contract.Count).IsEqualTo(operationIds);
        await Assert.That(served.Count).IsGreaterThan(0);
        await Assert.That(mismatches).IsEmpty();
        var unserved = contract.Keys.Where(key => key.Path.StartsWith("/v1/terrascale/", StringComparison.Ordinal) && !served.Contains(key) && !planned.Contains(key)).Select(key => key.Id).ToArray();
        await Assert.That(served.Where(planned.Contains).Select(key => key.Id).ToArray()).IsEmpty();
        await Assert.That(unserved).IsEmpty();
    }

    [Test]
    public async Task Scalar_is_served_from_this_origin()
    {
        await using var server = await AuthTestServer.StartAsync();

        using var redirect = await server.Client.GetAsync("/scalar");
        await Assert.That(redirect.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(new Uri(new Uri(server.BaseAddress, "/scalar"), redirect.Headers.Location!).AbsolutePath).IsEqualTo("/scalar/");
        using var response = await server.Client.GetAsync("/scalar/");
        var html = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(html).Contains("\"url\":\"openapi/v1.json\"");
        await Assert.That(html).DoesNotContain("cdn.jsdelivr.net");
        foreach (Match script in ScriptSource().Matches(html))
        {
            var source = new Uri(new Uri(server.BaseAddress, "/scalar/"), script.Groups["src"].Value);
            await Assert.That(source.Authority).IsEqualTo(server.BaseAddress.Authority);
            using var asset = await server.Client.GetAsync(source);
            await Assert.That(asset.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
    }

    [Test]
    [Arguments(ColorScheme.Light, 1440, 900)]
    [Arguments(ColorScheme.Dark, 1440, 900)]
    [Arguments(ColorScheme.Light, 390, 844)]
    [Arguments(ColorScheme.Dark, 390, 844)]
    public async Task Scalar_renders_the_contract_without_browser_errors(ColorScheme colorScheme, int width, int height)
    {
        await using var server = await AuthTestServer.StartAsync();
        var browser = await Chromium.BrowserAsync();
        await using var context = await browser.NewContextAsync(new()
        {
            ColorScheme = colorScheme,
            ViewportSize = new() { Width = width, Height = height },
        });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                errors.Add(message.Text);
            }
        };
        page.PageError += (_, error) => errors.Add(error);
        var external = new List<string>();
        page.Request += (_, request) =>
        {
            var url = new Uri(request.Url);
            var sameOrigin = url.Scheme == server.BaseAddress.Scheme
                && url.Host == server.BaseAddress.Host
                && url.Port == server.BaseAddress.Port;
            if (!sameOrigin && url.Scheme != "data")
            {
                external.Add(request.Url);
            }
        };

        await page.GotoAsync(new Uri(server.BaseAddress, "/scalar").ToString());
        try
        {
            await page.GetByRole(AriaRole.Heading, new() { Name = "TerraScale Auth API" }).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 20_000 });
        }
        catch (TimeoutException)
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, "scalar-failure.png"), FullPage = true });
            throw new TimeoutException($"Scalar did not render. Errors: {string.Join(" | ", errors)}. Body: {await page.InnerTextAsync("body")}");
        }

        await Assert.That(errors).IsEmpty();
        await Assert.That(external).IsEmpty();
    }

    [GeneratedRegex("<script[^>]*\\ssrc=\"(?<src>[^\"]+)\"")]
    private static partial Regex ScriptSource();

    [GeneratedRegex("^  (?<path>/[^:]*):$")]
    private static partial Regex ContractPath();

    [GeneratedRegex("^    (?<method>get|put|post|delete|patch|head|options|trace):$")]
    private static partial Regex ContractMethod();

    [GeneratedRegex("^    [^ ]")]
    private static partial Regex ContractPathMember();

    [GeneratedRegex("""^      operationId: (?<quote>['"]?)(?<id>[A-Za-z0-9_.-]+)\k<quote>$""")]
    private static partial Regex ContractOperation();

    [GeneratedRegex("""^(?<indent>  |        )- (?<scheme>[A-Za-z0-9]+): \[\]$""")]
    private static partial Regex ContractSecurity();

    [GeneratedRegex("""^        (?<quote>['"]?)(?<status>[1-5][0-9]{2})\k<quote>:""")]
    private static partial Regex ContractResponse();
}
