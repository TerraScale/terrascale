using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAuthProjectsContractTests
{
    private const string Token = "fixture-sign-in";

    [Test]
    [Arguments("get_settings")]
    [Arguments("update_settings")]
    [Arguments("set_provider")]
    [Arguments("delete_provider")]
    [Arguments("list_users")]
    [Arguments("export_users")]
    [Arguments("get_user")]
    [Arguments("disable_user")]
    [Arguments("enable_user")]
    [Arguments("delete_user")]
    [Arguments("list_sessions")]
    [Arguments("revoke_session")]
    [Arguments("list_clients")]
    [Arguments("create_client")]
    [Arguments("delete_client")]
    public async Task ProjectTenantRoutesMatchFixture(string operation)
    {
        var ids = new TerraScaleIdGenerator();
        var (project, user, session, clientId) = (ids.NewId(), ids.NewId(), ids.NewId(), ids.NewId());
        var fixtureJson = (await File.ReadAllTextAsync(ContractPath("fixtures/project-tenant.json")))
            .Replace("{project}", project, StringComparison.Ordinal).Replace("{user}", user, StringComparison.Ordinal)
            .Replace("{session}", session, StringComparison.Ordinal).Replace("{client}", clientId, StringComparison.Ordinal);
        using var fixtures = JsonDocument.Parse(fixtureJson);
        var fixture = fixtures.RootElement.GetProperty(operation);
        var requestJson = fixture.GetProperty("request").ValueKind == JsonValueKind.Null ? null : fixture.GetProperty("request").GetRawText();
        var responseJson = fixture.GetProperty("response").ValueKind == JsonValueKind.Null ? "" : fixture.GetProperty("response").GetRawText();
        using var handler = new CaptureHandler((HttpStatusCode)fixture.GetProperty("status").GetInt32(), responseJson);
        using var http = new HttpClient(handler);
        var projects = Client(http).Projects;
        var json = TerraScaleAuthIntegrationJsonContext.Default;
        Task call = operation switch
        {
            "get_settings" => projects.GetSettingsAsync(Token, project),
            "update_settings" => projects.UpdateSettingsAsync(Token, project, JsonSerializer.Deserialize(requestJson!, json.TerraScaleAuthProjectSettingsUpdate)!),
            "set_provider" => projects.SetProviderAsync(Token, project, "google", JsonSerializer.Deserialize(requestJson!, json.TerraScaleAuthProjectProviderUpdate)!),
            "delete_provider" => projects.DeleteProviderAsync(Token, project, "google"),
            "list_users" => projects.ListUsersAsync(Token, project, query: "synthetic@example", limit: 2, after: "opaque+/cursor="),
            "export_users" => projects.ExportUsersAsync(Token, project, limit: 2, after: "opaque+/cursor="),
            "get_user" => projects.GetUserAsync(Token, project, user),
            "disable_user" => projects.DisableUserAsync(Token, project, user),
            "enable_user" => projects.EnableUserAsync(Token, project, user),
            "delete_user" => projects.DeleteUserAsync(Token, project, user),
            "list_sessions" => projects.ListSessionsAsync(Token, project, userId: user, limit: 2, after: "opaque+/cursor="),
            "revoke_session" => projects.RevokeSessionAsync(Token, project, session),
            "list_clients" => projects.ListClientsAsync(Token, project),
            "create_client" => projects.CreateClientAsync(Token, project, JsonSerializer.Deserialize(requestJson!, json.TerraScaleAuthProjectCreateOAuthClient)!),
            "delete_client" => projects.DeleteClientAsync(Token, project, clientId),
            _ => throw new ArgumentException("Unknown contract operation.", nameof(operation))
        };
        await call;
        string? resultJson = call switch
        {
            Task<TerraScaleAuthProjectSettings> typed => JsonSerializer.Serialize(await typed, json.TerraScaleAuthProjectSettings),
            Task<TerraScaleAuthProjectUser> typed => JsonSerializer.Serialize(await typed, json.TerraScaleAuthProjectUser),
            Task<TerraScaleAuthProjectOAuthClient> typed => JsonSerializer.Serialize(await typed, json.TerraScaleAuthProjectOAuthClient),
            Task<TerraScaleAuthUserExportPage> typed => JsonSerializer.Serialize(await typed, json.TerraScaleAuthUserExportPage),
            Task<TerraScaleAuthPlatformPage<TerraScaleAuthProjectUser>> typed => JsonSerializer.Serialize(await typed, json.TerraScaleAuthPlatformPageTerraScaleAuthProjectUser),
            Task<TerraScaleAuthPlatformPage<TerraScaleAuthProjectSession>> typed => JsonSerializer.Serialize(await typed, json.TerraScaleAuthPlatformPageTerraScaleAuthProjectSession),
            Task<TerraScaleAuthPlatformPage<TerraScaleAuthProjectOAuthClient>> typed => JsonSerializer.Serialize(await typed, json.TerraScaleAuthPlatformPageTerraScaleAuthProjectOAuthClient),
            _ => null
        };
        if (resultJson is not null)
            await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(resultJson), JsonNode.Parse(responseJson))).IsTrue();
        else await Assert.That(responseJson).IsEmpty();
        await Assert.That(handler.Uri!.PathAndQuery).IsEqualTo(fixture.GetProperty("path").GetString());
        await Assert.That(handler.Method).IsEqualTo(fixture.GetProperty("method").GetString());
        await Assert.That(handler.Calls).IsEqualTo(1);
        await Assert.That(handler.Authorization!.ToString()).IsEqualTo("Bearer " + Token);
        if (requestJson is not null)
        {
            await Assert.That(handler.MediaType).IsEqualTo("application/json");
            await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(handler.Body!), JsonNode.Parse(requestJson))).IsTrue();
        }
        else await Assert.That(handler.Body).IsNull();
    }

    [Test]
    public async Task SettingsUpdateOmitsUnchangedPartsAndSendsNullBrandingToClear()
    {
        var project = new TerraScaleIdGenerator().NewId();
        using var handler = new CaptureHandler(HttpStatusCode.OK, $$$"""
            {"project_id":"{{{project}}}","issuer":"i","discovery_url":"d","sign_in_url":"s","methods":{"passcode":true,"password":true,"sign_up":true},
            "branding":{"name":null,"logo_url":null,"theme_color":null},"counts":{"users":0,"clients":0},"providers":[]}
            """);
        using var http = new HttpClient(handler);
        var settings = await Client(http).Projects.UpdateSettingsAsync(Token, project, new(Branding: new(null, null, null)));
        await Assert.That(settings.Branding.Name).IsNull();
        await Assert.That(handler.Body).IsEqualTo("""{"branding":{"name":null,"logo_url":null,"theme_color":null}}""");
    }

    [Test]
    public async Task HostedBrandingAndConfigurationCrossTheSdkHttpBoundary()
    {
        var project = new TerraScaleIdGenerator().NewId();
        using var handler = new CaptureHandler(HttpStatusCode.OK, $$$"""
            {"project_id":"{{{project}}}","issuer":"i","discovery_url":"d","sign_in_url":"s","methods":{"passcode":true,"password":true,"sign_up":true},
            "branding":{"name":"Paperwork","logo_url":null,"theme_color":"#2158e8","title_size":30,"default_theme":"dark","footer_links":[{"label":"Privacy","url":"https://example.test/privacy"}],"background_color":"#151515","text_color":"#fafafa"},
            "counts":{"users":0,"clients":0},"providers":[],"configuration":{"redirect_urls":["https://example.test/callback"],"session_lifetime_seconds":3600}}
            """);
        using var http = new HttpClient(handler);
        var result = await Client(http).Projects.UpdateSettingsAsync(Token, project,
            new(Branding: new("Paperwork", null, "#2158e8", 30, "dark", [new("Privacy", "https://example.test/privacy")], "#151515", "#fafafa"),
                Configuration: new(["https://example.test/callback"], 3600)));
        using var sent = JsonDocument.Parse(handler.Body!);
        await Assert.That(sent.RootElement.TryGetProperty("methods", out _)).IsFalse();
        await Assert.That(sent.RootElement.GetProperty("branding").GetProperty("title_size").GetInt32()).IsEqualTo(30);
        await Assert.That(sent.RootElement.GetProperty("configuration").GetProperty("session_lifetime_seconds").GetInt32()).IsEqualTo(3600);
        await Assert.That(result.Branding.DefaultTheme).IsEqualTo("dark");
        await Assert.That(sent.RootElement.GetProperty("branding").GetProperty("background_color").GetString()).IsEqualTo("#151515");
        await Assert.That(result.Branding.TextColor).IsEqualTo("#fafafa");
        await Assert.That(result.Branding.FooterLinks![0].Label).IsEqualTo("Privacy");
        await Assert.That(result.Configuration!.RedirectUrls).IsEquivalentTo(["https://example.test/callback"]);
        await Assert.That(handler.Authorization!.ToString()).IsEqualTo("Bearer " + Token);
        await Assert.That(handler.Uri!.AbsolutePath).IsEqualTo($"/v1/{project}/settings");
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task RoleTooLowSurfacesForbiddenOnceWithoutRetry()
    {
        var project = new TerraScaleIdGenerator().NewId();
        using var handler = new CaptureHandler(HttpStatusCode.Forbidden, """{"error":"forbidden","error_description":"admin role required"}""");
        using var http = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(() => Client(http).Projects.DeleteUserAsync(Token, project, project));
        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(exception.ErrorCode).IsEqualTo("forbidden");
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task InvalidIdsAndBoundsAreRejectedBeforeDispatch()
    {
        var project = new TerraScaleIdGenerator().NewId();
        using var handler = new CaptureHandler(HttpStatusCode.OK, "{}");
        using var http = new HttpClient(handler);
        var projects = Client(http).Projects;
        await Assert.ThrowsAsync<ArgumentException>(() => projects.GetSettingsAsync(Token, "../terrascale"));
        await Assert.ThrowsAsync<ArgumentException>(() => projects.GetUserAsync(Token, project, "not-an-id"));
        await Assert.ThrowsAsync<ArgumentException>(() => projects.ListSessionsAsync(Token, project, userId: "not-an-id"));
        await Assert.ThrowsAsync<ArgumentException>(() => projects.ListUsersAsync(Token, project, query: ""));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => projects.ListUsersAsync(Token, project, limit: 101));
        await Assert.That(handler.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task UserExportFollowsTheEscapedCursorAcrossTwoPagesWithOneRequestEach()
    {
        var ids = new TerraScaleIdGenerator();
        var (project, first, second) = (ids.NewId(), ids.NewId(), ids.NewId());
        const string cursor = "eyJj+/=&x";
        using var handler = new CaptureHandler(HttpStatusCode.OK,
            $$"""{"items":[{"id":"{{first}}","email":"a@example.test","email_verified":true,"created_at":"2026-10-13T00:00:00+00:00","disabled":false,"linked_providers":[]}],"next":"{{cursor}}","created_before":"2026-10-25T00:00:00+00:00"}""",
            $$"""{"items":[{"id":"{{second}}","email":"b@example.test","email_verified":true,"created_at":"2026-10-14T00:00:00+00:00","disabled":true,"linked_providers":[{"provider":"google","client_id":"public-client","subject":"sub"}]}],"next":null,"created_before":"2026-10-25T00:00:00+00:00"}""");
        using var http = new HttpClient(handler);
        var projects = Client(http).Projects;
        var page = await projects.ExportUsersAsync(Token, project, limit: 1);
        await Assert.That(handler.Uri!.PathAndQuery).IsEqualTo($"/v1/{project}/users/export?limit=1");
        await Assert.That(page.Next).IsEqualTo(cursor);
        var last = await projects.ExportUsersAsync(Token, project, limit: 1, after: page.Next);
        await Assert.That(handler.Uri!.PathAndQuery).IsEqualTo($"/v1/{project}/users/export?limit=1&after=eyJj%2B%2F%3D%26x");
        await Assert.That(last.Next).IsNull();
        await Assert.That(last.CreatedBefore).IsEqualTo(page.CreatedBefore);
        await Assert.That(last.Items[0].Id).IsEqualTo(second);
        await Assert.That(last.Items[0].Disabled).IsTrue();
        await Assert.That(last.Items[0].LinkedProviders[0]).IsEqualTo(new TerraScaleAuthLinkedProvider("google", "public-client", "sub"));
        await Assert.That(handler.Calls).IsEqualTo(2);
        await Assert.That(handler.Method).IsEqualTo("GET");
        await Assert.That(handler.Body).IsNull();
    }

    [Test]
    [Arguments(HttpStatusCode.Forbidden, "forbidden_role")]
    [Arguments(HttpStatusCode.NotFound, "not_found")]
    [Arguments(HttpStatusCode.ServiceUnavailable, "temporarily_unavailable")]
    public async Task UserExportRefusalSurfacesOnceWithoutRetry(HttpStatusCode status, string code)
    {
        var project = new TerraScaleIdGenerator().NewId();
        using var handler = new CaptureHandler(status, $$"""{"error":"{{code}}","error_description":"refused"}""");
        using var http = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(() => Client(http).Projects.ExportUsersAsync(Token, project));
        await Assert.That(exception!.StatusCode).IsEqualTo(status);
        await Assert.That(exception.ErrorCode).IsEqualTo(code);
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task UserExportRejectsInvalidInputBeforeDispatchAndHonoursCancellation()
    {
        var project = new TerraScaleIdGenerator().NewId();
        using var handler = new CaptureHandler(HttpStatusCode.OK, "{}");
        using var http = new HttpClient(handler);
        var projects = Client(http).Projects;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => projects.ExportUsersAsync(Token, project, limit: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => projects.ExportUsersAsync(Token, project, limit: 101));
        await Assert.ThrowsAsync<ArgumentException>(() => projects.ExportUsersAsync(Token, project, after: ""));
        await Assert.ThrowsAsync<ArgumentException>(() => projects.ExportUsersAsync(Token, project, after: new string('a', 8193)));
        await Assert.ThrowsAsync<ArgumentException>(() => projects.ExportUsersAsync(Token, "../terrascale"));
        await Assert.ThrowsAsync<ArgumentException>(() => projects.ExportUsersAsync(" ", project));
        await Assert.ThrowsAsync<ArgumentException>(() => projects.ExportUsersAsync("token with space", project));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => projects.ExportUsersAsync(Token, project, cancellationToken: cancelled.Token));
        await Assert.That(handler.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task BrandingEmailMessageRoundTripsAndIsOmittedWhenNull()
    {
        var project = new TerraScaleIdGenerator().NewId();
        using var handler = new CaptureHandler(HttpStatusCode.OK, $$$"""
            {"project_id":"{{{project}}}","issuer":"i","discovery_url":"d","sign_in_url":"s","methods":{"passcode":true,"password":true,"sign_up":true},
            "branding":{"name":"Paperwork","logo_url":null,"theme_color":"#2158e8","email_message":"Welcome <b>{{name}}</b>\nSee you soon."},"counts":{"users":0,"clients":0},"providers":[]}
            """);
        using var http = new HttpClient(handler);
        var projects = Client(http).Projects;
        const string message = "Welcome <b>{{name}}</b>\nSee you soon.";
        var result = await projects.UpdateSettingsAsync(Token, project, new(Branding: new("Paperwork", null, "#2158e8") { EmailMessage = message }));
        using var sent = JsonDocument.Parse(handler.Body!);
        await Assert.That(sent.RootElement.GetProperty("branding").GetProperty("email_message").GetString()).IsEqualTo(message);
        await Assert.That(result.Branding.EmailMessage).IsEqualTo(message);
        await projects.UpdateSettingsAsync(Token, project, new(Branding: new("Paperwork", null, null)));
        await Assert.That(handler.Body).IsEqualTo("""{"branding":{"name":"Paperwork","logo_url":null,"theme_color":null}}""");
    }

    [Test]
    public async Task ClientSecretIsRedactedFromFormatting()
    {
        const string secret = "fixture-client-secret-canary";
        var created = new TerraScaleAuthProjectOAuthClient("fixture-id", "Synthetic app", "confidential", ["https://example.test/cb"], DateTimeOffset.UnixEpoch, secret);
        await Assert.That(created.ToString()).DoesNotContain(secret);
    }

    private static string ContractPath(string relative, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../contracts/auth", relative));

    private static TerraScaleAuthClient Client(HttpClient http) => new(http, new TerraScaleAuthClientOptions
    {
        BaseAddress = new Uri("https://auth.example.test"),
        InstanceSlug = "terrascale",
    });

    private sealed class CaptureHandler(HttpStatusCode status, params string[] bodies) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? Method { get; private set; }
        public string? Body { get; private set; }
        public string? MediaType { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Uri = request.RequestUri; Method = request.Method.Method; Authorization = request.Headers.Authorization;
            MediaType = request.Content?.Headers.ContentType?.MediaType;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(bodies[Math.Min(Calls, bodies.Length) - 1], Encoding.UTF8, "application/json") };
        }
    }
}
