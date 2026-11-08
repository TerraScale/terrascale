using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAuthSsoContractTests
{
    private const string Org = "01ARZ3NDEKTSV4RRFFQ69G5FA6";
    private const string Secret = "oidc-secret-fixture-value";

    [Test]
    public async Task SsoConnectionMatchesFixture()
    {
        using var fixtures = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "contracts/auth/fixtures/sso.json")));
        var put = fixtures.RootElement.GetProperty("put_sso_connection");
        var get = fixtures.RootElement.GetProperty("get_sso_connection");
        var verify = fixtures.RootElement.GetProperty("verify_sso_domain");
        var delete = fixtures.RootElement.GetProperty("delete_sso_connection");
        using var handler = new SsoHandler(put, get);
        using var http = new HttpClient(handler);
        var client = Client(http);
        var request = new TerraScaleAuthPlatformSsoConnectionRequest("Example.com.", "oidc", "member",
            new TerraScaleAuthPlatformSsoOidcRequest(" https://issuer.example.test ", "ts-client", Secret));
        await Assert.That(request.ToString()).DoesNotContain(Secret);
        await Assert.That(request.ToString()).Contains("[REDACTED]");

        var created = await client.Platform.PutSsoConnectionAsync("fixture-sign-in", Org, request);
        await Assert.That(handler.LastMethod).IsEqualTo(put.GetProperty("method").GetString());
        await Assert.That(handler.LastPath).IsEqualTo(Route(put));
        await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(handler.LastBody!), JsonNode.Parse(
            put.GetProperty("request").GetRawText().Replace("{secret}", Secret)))).IsTrue();
        await Assert.That(handler.LastBody!.Contains(Secret)).IsTrue();
        await Assert.That(created.OrganizationId).IsEqualTo(Org);
        await Assert.That(created.Domain).IsEqualTo("example.com");
        await Assert.That(created.DomainVerified).IsFalse();
        await Assert.That(created.Verification!.Name).IsEqualTo("_terrascale-sso.example.com");
        await Assert.That(created.Oidc!.ClientSecretSet).IsTrue();
        await Assert.That(created.Oidc.RedirectUri).IsEqualTo("https://auth.terrascale.tech/ui/terrascale/sso/callback");
        await Assert.That(created.CreatedAt).IsEqualTo(DateTimeOffset.Parse("2026-11-01T00:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture));

        var stored = await client.Platform.GetSsoConnectionAsync("fixture-sign-in", Org);
        await Assert.That(handler.LastMethod).IsEqualTo(get.GetProperty("method").GetString());
        await Assert.That(handler.LastPath).IsEqualTo(Route(get));
        await Assert.That(stored.DomainVerified).IsTrue();
        await Assert.That(stored.VerifiedAt).IsNotNull();
        await Assert.That(stored.Oidc!.Issuer).IsEqualTo(created.Oidc.Issuer);

        var verified = await client.Platform.VerifySsoConnectionAsync("fixture-sign-in", Org);
        await Assert.That(handler.LastMethod).IsEqualTo(verify.GetProperty("method").GetString());
        await Assert.That(handler.LastPath).IsEqualTo(Route(verify));
        await Assert.That(verified.DomainVerified).IsTrue();

        await client.Platform.DeleteSsoConnectionAsync("fixture-sign-in", Org);
        await Assert.That(handler.LastMethod).IsEqualTo(delete.GetProperty("method").GetString());
        await Assert.That(handler.LastPath).IsEqualTo(Route(delete));
        await Assert.That(handler.Statuses).IsEquivalentTo(new[] { HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.NoContent });
    }

    private static string Route(JsonElement operation) => operation.GetProperty("path").GetString()!.Replace("{org}", Org);

    private static TerraScaleAuthClient Client(HttpClient http) => new(http, new TerraScaleAuthClientOptions
    {
        BaseAddress = new Uri("https://auth.example.test"),
        InstanceSlug = "terrascale",
    });

    private sealed class SsoHandler(JsonElement put, JsonElement get) : HttpMessageHandler
    {
        public string? LastMethod { get; private set; }
        public string? LastPath { get; private set; }
        public string? LastBody { get; private set; }
        public List<HttpStatusCode> Statuses { get; } = [];
        private HttpStatusCode status = HttpStatusCode.NoContent;
        private string body = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastMethod = request.Method.Method;
            LastPath = request.RequestUri!.AbsolutePath;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (request.Method.Method == "PUT")
            {
                status = (HttpStatusCode)put.GetProperty("status").GetInt32();
                body = put.GetProperty("response").GetRawText().Replace("{org}", Org);
            }
            else if (request.Method.Method is "GET" or "POST")
            {
                status = (HttpStatusCode)get.GetProperty("status").GetInt32();
                body = get.GetProperty("response").GetRawText().Replace("{org}", Org);
            }
            else
            {
                status = HttpStatusCode.NoContent;
                body = "";
            }
            Statuses.Add(status);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
