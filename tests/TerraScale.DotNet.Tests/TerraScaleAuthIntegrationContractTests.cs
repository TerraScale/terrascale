using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAuthIntegrationContractTests
{
    private const string ClientId = "fixture id:é+";
    private const string ClientSecret = "fixture secret:+=/é";

    [Test]
    [Arguments("create_organization")]
    [Arguments("list_organizations")]
    [Arguments("get_organization")]
    [Arguments("rename_organization")]
    [Arguments("delete_organization")]
    [Arguments("list_members")]
    [Arguments("change_member_role")]
    [Arguments("remove_member")]
    [Arguments("leave_organization")]
    [Arguments("create_project")]
    [Arguments("list_projects")]
    [Arguments("get_project")]
    [Arguments("rename_project")]
    [Arguments("delete_project")]
    [Arguments("list_project_members")]
    [Arguments("set_project_member_role")]
    [Arguments("remove_project_member")]
    [Arguments("create_organization_api_key")]
    [Arguments("list_organization_api_keys")]
    [Arguments("get_organization_api_key")]
    [Arguments("revoke_organization_api_key")]
    [Arguments("rotate_organization_api_key")]
    [Arguments("create_project_api_key")]
    [Arguments("list_project_api_keys")]
    [Arguments("get_project_api_key")]
    [Arguments("revoke_project_api_key")]
    [Arguments("rotate_project_api_key")]
    [Arguments("headless_email_code_start")]
    [Arguments("headless_sign_up")]
    [Arguments("headless_password_reset_start")]
    [Arguments("headless_password_reset_complete")]
    public async Task SourcePinnedPlatformAndHeadlessRoutesMatch(string operation)
    {
        var ids = new TerraScaleIdGenerator();
        var organizationId = ids.NewId(); var projectId = ids.NewId(); var userId = ids.NewId(); var keyId = ids.NewId();
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(ContractPath("routes.json")));
        var contract = manifest.RootElement.GetProperty("operations").EnumerateArray().Single(item => item.GetProperty("operation_id").GetString() == operation);
        var fixtureJson = await File.ReadAllTextAsync(ContractPath("fixtures/platform-headless.json"));
        fixtureJson = fixtureJson.Replace("{organization}", organizationId, StringComparison.Ordinal).Replace("{project}", projectId, StringComparison.Ordinal)
            .Replace("{user}", userId, StringComparison.Ordinal).Replace("{key}", keyId, StringComparison.Ordinal);
        using var fixtures = JsonDocument.Parse(fixtureJson);
        var fixture = fixtures.RootElement.GetProperty(operation);
        var requestJson = fixture.GetProperty("request").ValueKind == JsonValueKind.Null ? null : fixture.GetProperty("request").GetRawText();
        var responseJson = fixture.GetProperty("response").ValueKind == JsonValueKind.Null ? "" : fixture.GetProperty("response").GetRawText();
        using var handler = new CaptureHandler((HttpStatusCode)contract.GetProperty("success_status").GetInt32(), responseJson);
        using var http = new HttpClient(handler);
        var client = Client(http);
        var cancellationToken = CancellationToken.None;
        Task call = operation switch
        {
            "create_organization" => client.Platform.CreateOrganizationAsync("fixture-sign-in", JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName)!, cancellationToken: cancellationToken),
            "list_organizations" => client.Platform.ListOrganizationsAsync("fixture-sign-in", limit: 2, after: "opaque+/cursor=", cancellationToken: cancellationToken),
            "get_organization" => client.Platform.GetOrganizationAsync("fixture-sign-in", organizationId, cancellationToken: cancellationToken),
            "rename_organization" => client.Platform.RenameOrganizationAsync("fixture-sign-in", organizationId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName)!, cancellationToken: cancellationToken),
            "delete_organization" => client.Platform.DeleteOrganizationAsync("fixture-sign-in", organizationId, cancellationToken: cancellationToken),
            "list_members" => client.Platform.ListMembersAsync("fixture-sign-in", organizationId, limit: 2, after: "opaque+/cursor=", cancellationToken: cancellationToken),
            "change_member_role" => client.Platform.ChangeMemberRoleAsync("fixture-sign-in", organizationId, userId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformRole)!, cancellationToken: cancellationToken),
            "remove_member" => client.Platform.RemoveMemberAsync("fixture-sign-in", organizationId, userId, cancellationToken: cancellationToken),
            "leave_organization" => client.Platform.LeaveOrganizationAsync("fixture-sign-in", organizationId, cancellationToken: cancellationToken),
            "create_project" => client.Platform.CreateProjectAsync("fixture-sign-in", organizationId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName)!, cancellationToken: cancellationToken),
            "list_projects" => client.Platform.ListProjectsAsync("fixture-sign-in", organizationId, limit: 2, after: "opaque+/cursor=", cancellationToken: cancellationToken),
            "get_project" => client.Platform.GetProjectAsync("fixture-sign-in", organizationId, projectId, cancellationToken: cancellationToken),
            "rename_project" => client.Platform.RenameProjectAsync("fixture-sign-in", organizationId, projectId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName)!, cancellationToken: cancellationToken),
            "delete_project" => client.Platform.DeleteProjectAsync("fixture-sign-in", organizationId, projectId, cancellationToken: cancellationToken),
            "list_project_members" => client.Platform.ListProjectMembersAsync("fixture-sign-in", organizationId, projectId, limit: 2, after: "opaque+/cursor=", cancellationToken: cancellationToken),
            "set_project_member_role" => client.Platform.SetProjectMemberRoleAsync("fixture-sign-in", organizationId, projectId, userId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformRole)!, cancellationToken: cancellationToken),
            "remove_project_member" => client.Platform.RemoveProjectMemberAsync("fixture-sign-in", organizationId, projectId, userId, cancellationToken: cancellationToken),
            "create_organization_api_key" => client.Platform.CreateOrganizationApiKeyAsync("fixture-sign-in", organizationId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformCreateApiKey)!, cancellationToken: cancellationToken),
            "list_organization_api_keys" => client.Platform.ListOrganizationApiKeysAsync("fixture-sign-in", organizationId, limit: 2, after: "opaque+/cursor=", cancellationToken: cancellationToken),
            "get_organization_api_key" => client.Platform.GetOrganizationApiKeyAsync("fixture-sign-in", organizationId, keyId, cancellationToken: cancellationToken),
            "revoke_organization_api_key" => client.Platform.RevokeOrganizationApiKeyAsync("fixture-sign-in", organizationId, keyId, cancellationToken: cancellationToken),
            "rotate_organization_api_key" => client.Platform.RotateOrganizationApiKeyAsync("fixture-sign-in", organizationId, keyId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformRotateApiKey)!, cancellationToken: cancellationToken),
            "create_project_api_key" => client.Platform.CreateProjectApiKeyAsync("fixture-sign-in", organizationId, projectId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformCreateApiKey)!, cancellationToken: cancellationToken),
            "list_project_api_keys" => client.Platform.ListProjectApiKeysAsync("fixture-sign-in", organizationId, projectId, limit: 2, after: "opaque+/cursor=", cancellationToken: cancellationToken),
            "get_project_api_key" => client.Platform.GetProjectApiKeyAsync("fixture-sign-in", organizationId, projectId, keyId, cancellationToken: cancellationToken),
            "revoke_project_api_key" => client.Platform.RevokeProjectApiKeyAsync("fixture-sign-in", organizationId, projectId, keyId, cancellationToken: cancellationToken),
            "rotate_project_api_key" => client.Platform.RotateProjectApiKeyAsync("fixture-sign-in", organizationId, projectId, keyId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformRotateApiKey)!, cancellationToken: cancellationToken),
            "headless_email_code_start" => client.Headless.StartEmailCodeAsync(ClientId, ClientSecret, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthHeadlessEmailCode)!, cancellationToken: cancellationToken),
            "headless_sign_up" => client.Headless.SignUpAsync(ClientId, ClientSecret, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthHeadlessSignUp)!, cancellationToken: cancellationToken),
            "headless_password_reset_start" => client.Headless.StartPasswordResetAsync(ClientId, ClientSecret, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthHeadlessEmail)!, cancellationToken: cancellationToken),
            "headless_password_reset_complete" => client.Headless.CompletePasswordResetAsync(ClientId, ClientSecret, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthHeadlessPasswordReset)!, cancellationToken: cancellationToken),
            _ => throw new ArgumentException("Unknown contract operation.", nameof(operation))
        };
        await call;
        var resultJson = await ResultJsonAsync(call);
        if (resultJson is not null)
            await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(resultJson), JsonNode.Parse(responseJson))).IsTrue();
        var path = contract.GetProperty("path").GetString()!.Replace("{slug}", "terrascale", StringComparison.Ordinal)
            .Replace("{organization}", organizationId, StringComparison.Ordinal).Replace("{project}", projectId, StringComparison.Ordinal)
            .Replace("{user}", userId, StringComparison.Ordinal).Replace("{key}", keyId, StringComparison.Ordinal);
        if (contract.GetProperty("pagination").ValueKind != JsonValueKind.Null) path += "?limit=2&after=opaque%2B%2Fcursor%3D";
        await Assert.That(handler.Uri!.PathAndQuery).IsEqualTo(path);
        await Assert.That(handler.Method).IsEqualTo(contract.GetProperty("method").GetString());
        await Assert.That(handler.Uri.Host).IsEqualTo("auth.example.test");
        await Assert.That(handler.Calls).IsEqualTo(1);
        if (operation.StartsWith("headless_", StringComparison.Ordinal))
        {
            await Assert.That(handler.Authorization!.Scheme).IsEqualTo("Basic");
            var credential = Encoding.ASCII.GetString(Convert.FromBase64String(handler.Authorization.Parameter!)).Split(':');
            await Assert.That(Uri.UnescapeDataString(credential[0].Replace("+", " ", StringComparison.Ordinal))).IsEqualTo(ClientId);
            await Assert.That(Uri.UnescapeDataString(credential[1].Replace("+", " ", StringComparison.Ordinal))).IsEqualTo(ClientSecret);
        }
        else await Assert.That(handler.Authorization!.ToString()).IsEqualTo("Bearer fixture-sign-in");
        if (requestJson is not null)
        {
            await Assert.That(handler.MediaType).IsEqualTo("application/json");
            await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(handler.Body!), JsonNode.Parse(requestJson))).IsTrue();
        }
        else await Assert.That(handler.Body).IsNull();
    }

    private static async Task<string?> ResultJsonAsync(Task call) => call switch
    {
        Task<TerraScaleAuthPlatformOrganization> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformOrganization),
        Task<TerraScaleAuthPlatformMember> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformMember),
        Task<TerraScaleAuthPlatformProject> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformProject),
        Task<TerraScaleAuthPlatformProjectMember> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformProjectMember),
        Task<TerraScaleAuthPlatformApiKey> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformApiKey),
        Task<TerraScaleAuthPlatformApiKeySecret> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformApiKeySecret),
        Task<TerraScaleAuthHeadlessPendingSignUp> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthHeadlessPendingSignUp),
        Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformOrganization>> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformPageTerraScaleAuthPlatformOrganization),
        Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformMember>> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformPageTerraScaleAuthPlatformMember),
        Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformProject>> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformPageTerraScaleAuthPlatformProject),
        Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformProjectMember>> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformPageTerraScaleAuthPlatformProjectMember),
        Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformApiKey>> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformPageTerraScaleAuthPlatformApiKey),
        _ => null
    };

    private static string ContractPath(string relative, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../contracts/auth", relative));

    private static TerraScaleAuthClient Client(HttpClient http) => new(http, new TerraScaleAuthClientOptions
    {
        BaseAddress = new Uri("https://auth.example.test"), InstanceSlug = "terrascale", MaxRetryAttempts = 2, RetryDelay = TimeSpan.Zero
    });

    private sealed class CaptureHandler(HttpStatusCode status, string body) : HttpMessageHandler
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
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
