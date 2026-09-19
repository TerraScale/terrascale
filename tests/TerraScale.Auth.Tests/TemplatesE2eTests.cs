namespace TerraScale.Auth.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;

/// <summary>JWT template management, minting, and claim substitution end to end.</summary>
[NotInParallel]
public sealed class TemplatesE2eTests
{
    private sealed record Context(AuthServiceFixture Service, string InstanceId, string Slug, string ApiKey, User User)
    {
        public AuthStore Store => Service.Store;

        public HttpClient Http => Service.Http;
    }

    private static async Task<Context> ArrangeAsync(string label)
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, _) = await service.CreateInstanceAsync("tpl-" + label + "-" + Guid.NewGuid().ToString("N")[..6]);

        // Instance creation stores sha256(secret-part) while InstanceResolver hashes the whole
        // bearer string, so the operator-issued key cannot resolve; arrange one that does.
        var secret = "sk_" + SecureTokens.RandomToken(40);
        await service.Store.CreateApiKeyAsync(new ApiKey
        {
            Id = "key_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            Name = "e2e",
            SecretKey = SecureTokens.Sha256Hex(secret),
            Scopes = ["*"],
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });

        var user = await new SessionService(service.Store).CreateUserAsync(
            instanceId, $"{label}-{Guid.NewGuid().ToString("N")[..6]}@example.com", $"u_{label}", null, "Template Tester");
        return new Context(service, instanceId, slug, secret, user);
    }

    private static HttpRequestMessage Request(Context c, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (c.ApiKey.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.ApiKey);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<JsonElement> SendAsync(Context c, HttpMethod method, string path, object? body, HttpStatusCode expected)
    {
        using var response = await c.Http.SendAsync(Request(c, method, path, body));
        await Assert.That(response.StatusCode).IsEqualTo(expected);
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private static object Template(string name, object claims, int? lifetime = null) =>
        lifetime is null ? new { name, claims } : new { name, claims, lifetime_seconds = lifetime };

    [Test]
    public async Task TemplateCrudLifecycle()
    {
        var c = await ArrangeAsync("crud");
        var claims = new { sub = "{{user.id}}", email = "{{user.email}}" };

        using (var unauthorized = await c.Http.SendAsync(Request(c with { ApiKey = "" }, HttpMethod.Get, $"/v1/{c.Slug}/jwt-templates")))
            await Assert.That(unauthorized.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/jwt-templates", Template("has space", claims), HttpStatusCode.BadRequest);
        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/jwt-templates", Template(new string('a', 49), claims), HttpStatusCode.BadRequest);
        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/jwt-templates", new { name = "noclaims" }, HttpStatusCode.BadRequest);

        var created = await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/jwt-templates", Template("Custom", claims), HttpStatusCode.OK);
        var id = created.GetProperty("id").GetString()!;
        await Assert.That(id.StartsWith("jtp_")).IsTrue();
        await Assert.That(created.GetProperty("name").GetString()).IsEqualTo("custom");
        await Assert.That(created.GetProperty("lifetime_seconds").GetInt32()).IsEqualTo(300);
        await Assert.That(created.GetProperty("claims").GetProperty("sub").GetString()).IsEqualTo("{{user.id}}");

        var duplicate = await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/jwt-templates", Template("custom", claims), HttpStatusCode.Conflict);
        await Assert.That(duplicate.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("template_exists");

        var listed = await SendAsync(c, HttpMethod.Get, $"/v1/{c.Slug}/jwt-templates", null, HttpStatusCode.OK);
        await Assert.That(listed.EnumerateArray().Single(t => t.GetProperty("id").GetString() == id).GetProperty("name").GetString()).IsEqualTo("custom");

        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/jwt-templates", Template("other", claims), HttpStatusCode.OK);
        var renamedToOther = await SendAsync(c, HttpMethod.Patch, $"/v1/{c.Slug}/jwt-templates/custom", new { name = "other" }, HttpStatusCode.Conflict);
        await Assert.That(renamedToOther.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("template_exists");

        var patched = await SendAsync(c, HttpMethod.Patch, $"/v1/{c.Slug}/jwt-templates/custom",
            new { name = "renamed", claims = new { sub = "{{user.email}}" }, lifetime_seconds = 900 }, HttpStatusCode.OK);
        await Assert.That(patched.GetProperty("name").GetString()).IsEqualTo("renamed");
        await Assert.That(patched.GetProperty("lifetime_seconds").GetInt32()).IsEqualTo(900);
        await Assert.That(patched.GetProperty("claims").GetProperty("sub").GetString()).IsEqualTo("{{user.email}}");

        using (var deleted = await c.Http.SendAsync(Request(c, HttpMethod.Delete, $"/v1/{c.Slug}/jwt-templates/other")))
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/jwt-templates/other/tokens", new { user_id = c.User.Id }, HttpStatusCode.NotFound);
        using (var gone = await c.Http.SendAsync(Request(c, HttpMethod.Delete, $"/v1/{c.Slug}/jwt-templates/other")))
            await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await c.Store.GetJwtTemplateAsync(id))!.Name).IsEqualTo("renamed");
    }

    [Test]
    public async Task MintSubstitutesUserOrgAndUnknownPlaceholders()
    {
        var c = await ArrangeAsync("mint");
        var claims = new Dictionary<string, object>
        {
            ["sub"] = "{{user.id}}",
            ["email"] = "{{user.email}}",
            ["username"] = "{{user.username}}",
            ["name"] = "{{user.name}}",
            ["email_verified"] = "{{user.email_verified}}",
            ["org_id"] = "{{org.id}}",
            ["org_role"] = "{{org.role}}",
            ["custom"] = "x{{user.unknown}}y",
        };
        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/jwt-templates", Template("mintable", claims), HttpStatusCode.OK);

        // Before any membership, org placeholders render empty.
        var bare = await MintAsync(c, "mintable");
        await Assert.That(Bare(bare, "org_id")).IsEqualTo("");
        await Assert.That(Bare(bare, "custom")).IsEqualTo("xy");

        var org = await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/organizations", new { name = "Acme" }, HttpStatusCode.OK);
        var orgId = org.GetProperty("id").GetString()!;
        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/organizations/{orgId}/memberships", new { user_id = c.User.Id, role = "admin" }, HttpStatusCode.OK);

        var minted = await MintAsync(c, "mintable");
        await Assert.That(Bare(minted, "sub")).IsEqualTo(c.User.Id);
        await Assert.That(Bare(minted, "email")).IsEqualTo(c.User.Email);
        await Assert.That(Bare(minted, "username")).IsEqualTo(c.User.Username);
        await Assert.That(Bare(minted, "name")).IsEqualTo(c.User.Name);
        await Assert.That(Bare(minted, "email_verified")).IsEqualTo("false");
        await Assert.That(Bare(minted, "org_id")).IsEqualTo(orgId);
        await Assert.That(Bare(minted, "org_role")).IsEqualTo("admin");

        // ES256: three segments and a 64-byte raw signature.
        await Assert.That(minted.Split('.').Length).IsEqualTo(3);
    }

    [Test]
    public async Task MintRejectsUnknownTemplateOrUser()
    {
        var c = await ArrangeAsync("404");
        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/jwt-templates", Template("real", new { sub = "{{user.id}}" }), HttpStatusCode.OK);

        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/jwt-templates/missing/tokens", new { user_id = c.User.Id }, HttpStatusCode.NotFound);
        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/jwt-templates/real/tokens", new { user_id = "usr_missing" }, HttpStatusCode.NotFound);
        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/jwt-templates/real/tokens", new { }, HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task CustomLifetimeIsRespectedInMintedToken()
    {
        var c = await ArrangeAsync("lifetime");
        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/jwt-templates", Template("longlived", new { sub = "{{user.id}}" }, 7200), HttpStatusCode.OK);

        var claims = JsonWebToken.DecodeUnverified(await MintAsync(c, "longlived"))!;
        var lifetime = ((JsonElement)claims["exp"]).GetInt64() - ((JsonElement)claims["iat"]).GetInt64();
        await Assert.That(lifetime).IsGreaterThanOrEqualTo(7199);
        await Assert.That(lifetime).IsLessThanOrEqualTo(7200);
    }

    // ---- Helpers ---------------------------------------------------------------------

    private static async Task<string> MintAsync(Context c, string name)
    {
        var body = await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/jwt-templates/{name}/tokens", new { user_id = c.User.Id }, HttpStatusCode.OK);
        return body.GetProperty("jwt").GetString()!;
    }

    /// <summary>Claim as a string from an unverified decode (JsonElement values).</summary>
    private static string Bare(string jwt, string claim) =>
        ((JsonElement)JsonWebToken.DecodeUnverified(jwt)![claim]).GetString()!;
}
