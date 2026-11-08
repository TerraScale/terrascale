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
    [Arguments("rotate_client_secret")]
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
            "create_client" => projects.CreateClientAsync(Token, project, JsonSerializer.Deserialize(requestJson!, json.TerraScaleAuthProjectCreateOAuthClient)!, "fixture-step-up"),
            "rotate_client_secret" => projects.RotateProjectClientSecretAsync(Token, project, clientId, "fixture-step-up", JsonSerializer.Deserialize(requestJson!, json.TerraScaleAuthProjectRotateOAuthClient)!),
            "delete_client" => projects.DeleteClientAsync(Token, project, clientId),
            _ => throw new ArgumentException("Unknown contract operation.", nameof(operation))
        };
        await call;
        await Assert.That(handler.StepUpProof).IsEqualTo(operation is "create_client" or "rotate_client_secret" ? "fixture-step-up" : null);
        string? resultJson = call switch
        {
            Task<TerraScaleAuthProjectSettings> typed => JsonSerializer.Serialize(await typed, json.TerraScaleAuthProjectSettings),
            Task<TerraScaleAuthProjectUser> typed => JsonSerializer.Serialize(await typed, json.TerraScaleAuthProjectUser),
            Task<TerraScaleAuthProjectOAuthClient> typed => JsonSerializer.Serialize(await typed, json.TerraScaleAuthProjectOAuthClient),
            Task<TerraScaleAuthProjectClientSecret> typed => JsonSerializer.Serialize(await typed, json.TerraScaleAuthProjectClientSecret),
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
        public string? StepUpProof { get; private set; }
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            StepUpProof = request.Headers.TryGetValues("X-TerraScale-Step-Up-Proof", out var proofs) ? proofs.Single() : null;
            Calls++; Uri = request.RequestUri; Method = request.Method.Method; Authorization = request.Headers.Authorization;
            MediaType = request.Content?.Headers.ContentType?.MediaType;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(bodies[Math.Min(Calls, bodies.Length) - 1], Encoding.UTF8, "application/json") };
        }
    }
}
