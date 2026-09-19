namespace TerraScale.Auth.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;

/// <summary>Organization enrollment domains: management CRUD and the enrollment hook.</summary>
[NotInParallel]
public sealed class OrgDomainsE2eTests
{
    private sealed record Context(AuthServiceFixture Service, string InstanceId, string Slug, string ApiKey, string OrgId)
    {
        public AuthStore Store => Service.Store;

        public HttpClient Http => Service.Http;
    }

    private static async Task<Context> ArrangeAsync(string label)
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, _) = await service.CreateInstanceAsync("orgdom-" + label + "-" + Guid.NewGuid().ToString("N")[..6]);

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

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/{slug}/organizations")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", secret) },
            Content = JsonContent.Create(new { name = "Acme " + label }),
        };
        using var response = await service.Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var orgId = (await response.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("id").GetString()!;
        return new Context(service, instanceId, slug, secret, orgId);
    }

    private static HttpRequestMessage Request(Context c, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", c.ApiKey) },
        };
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

    private static async Task<User> CreateUserAsync(Context c, string email) =>
        await new SessionService(c.Store).CreateUserAsync(c.InstanceId, email, null, null, null);

    [Test]
    public async Task DomainCrudLifecycle()
    {
        var c = await ArrangeAsync("crud");
        var secondOrg = await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/organizations", new { name = "Second" }, HttpStatusCode.OK);

        using (var unauthorized = await c.Http.PostAsJsonAsync($"/v1/{c.Slug}/organizations/{c.OrgId}/domains", new { domain = "acme.com" }))
            await Assert.That(unauthorized.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/organizations/{c.OrgId}/domains", new { domain = "not a domain" }, HttpStatusCode.BadRequest);
        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/organizations/{c.OrgId}/domains", new { domain = "-leadinghyphen.com" }, HttpStatusCode.BadRequest);
        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/organizations/{c.OrgId}/domains", new { domain = new string('a', 254) + ".com" }, HttpStatusCode.BadRequest);
        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/organizations/org_missing/domains", new { domain = "acme.com" }, HttpStatusCode.NotFound);

        var created = await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/organizations/{c.OrgId}/domains", new { domain = "Acme.COM" }, HttpStatusCode.OK);
        var domainId = created.GetProperty("id").GetString()!;
        await Assert.That(domainId.StartsWith("dom_")).IsTrue();
        await Assert.That(created.GetProperty("domain").GetString()).IsEqualTo("acme.com");
        await Assert.That(created.GetProperty("organization_id").GetString()).IsEqualTo(c.OrgId);

        var duplicate = await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/organizations/{c.OrgId}/domains", new { domain = "acme.com" }, HttpStatusCode.Conflict);
        await Assert.That(duplicate.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("domain_exists");
        // The domain is unique across the whole instance, not per organization.
        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/organizations/{secondOrg.GetProperty("id").GetString()}/domains", new { domain = "acme.com" }, HttpStatusCode.Conflict);

        var listed = await SendAsync(c, HttpMethod.Get, $"/v1/{c.Slug}/organizations/{c.OrgId}/domains", null, HttpStatusCode.OK);
        await Assert.That(listed.EnumerateArray().Single().GetProperty("id").GetString()).IsEqualTo(domainId);
        var otherList = await SendAsync(c, HttpMethod.Get, $"/v1/{c.Slug}/organizations/{secondOrg.GetProperty("id").GetString()}/domains", null, HttpStatusCode.OK);
        await Assert.That(otherList.GetArrayLength()).IsEqualTo(0);

        // A domain can only be deleted through its own organization.
        using (var wrongOrg = await c.Http.SendAsync(Request(c, HttpMethod.Delete, $"/v1/{c.Slug}/organizations/{secondOrg.GetProperty("id").GetString()}/domains/{domainId}")))
            await Assert.That(wrongOrg.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using (var deleted = await c.Http.SendAsync(Request(c, HttpMethod.Delete, $"/v1/{c.Slug}/organizations/{c.OrgId}/domains/{domainId}")))
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(await c.Store.GetOrgDomainAsync(domainId)).IsNull();
        using (var gone = await c.Http.SendAsync(Request(c, HttpMethod.Delete, $"/v1/{c.Slug}/organizations/{c.OrgId}/domains/{domainId}")))
            await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task DeletedOrganizationReleasesItsDomainForAnotherOrg()
    {
        var c = await ArrangeAsync("cascade");
        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/organizations/{c.OrgId}/domains", new { domain = "handover.example" }, HttpStatusCode.OK);

        using (var deleted = await c.Http.SendAsync(Request(c, HttpMethod.Delete, $"/v1/{c.Slug}/organizations/{c.OrgId}")))
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // The org is gone with its domain: a fresh org must be able to enroll the
        // same domain — a stale claim here would 409 forever with no owner behind it.
        var successor = await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/organizations", new { name = "Successor" }, HttpStatusCode.OK);
        var reenrolled = await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/organizations/{successor.GetProperty("id").GetString()}/domains", new { domain = "handover.example" }, HttpStatusCode.OK);
        await Assert.That(reenrolled.GetProperty("domain").GetString()).IsEqualTo("handover.example");
    }

    [Test]
    public async Task EnrollCreatesMembershipWritesAuditAndIsIdempotent()
    {
        var c = await ArrangeAsync("enroll");
        var user = await CreateUserAsync(c, $"member-{Guid.NewGuid().ToString("N")[..6]}@acme.com");
        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/organizations/{c.OrgId}/domains", new { domain = "acme.com" }, HttpStatusCode.OK);

        var enrolled = await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/domains/acme.com/enroll", new { user_id = user.Id }, HttpStatusCode.OK);
        await Assert.That(enrolled.GetProperty("org_id").GetString()).IsEqualTo(c.OrgId);

        var membership = (await c.Store.ListMembershipsByOrgAsync(c.OrgId)).Single();
        await Assert.That(membership.UserId).IsEqualTo(user.Id);
        await Assert.That(membership.Role).IsEqualTo("member");

        var audited = (await c.Store.ListAuditEventsAsync(c.InstanceId, 100)).Single(e => e.Type == "organization.enrolled");
        await Assert.That(audited.Data.TryGetValue("organization_id", out var orgId) && orgId.ToString() == c.OrgId).IsTrue();

        // Re-enrolling does not duplicate the membership.
        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/domains/acme.com/enroll", new { user_id = user.Id }, HttpStatusCode.OK);
        await Assert.That((await c.Store.ListMembershipsByOrgAsync(c.OrgId)).Count).IsEqualTo(1);
    }

    [Test]
    public async Task EnrollRejectsMismatchesAndUnknowns()
    {
        var c = await ArrangeAsync("reject");
        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/organizations/{c.OrgId}/domains", new { domain = "acme.com" }, HttpStatusCode.OK);

        var outsider = await CreateUserAsync(c, $"guest-{Guid.NewGuid().ToString("N")[..6]}@elsewhere.example");
        var noEmail = await new SessionService(c.Store).CreateUserAsync(c.InstanceId, null, null, null, null);

        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/domains/acme.com/enroll", new { user_id = outsider.Id }, HttpStatusCode.BadRequest);
        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/domains/acme.com/enroll", new { user_id = noEmail.Id }, HttpStatusCode.BadRequest);
        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/domains/unknown.example/enroll", new { user_id = outsider.Id }, HttpStatusCode.NotFound);
        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/domains/acme.com/enroll", new { user_id = "usr_missing" }, HttpStatusCode.NotFound);
        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/domains/acme.com/enroll", new { }, HttpStatusCode.BadRequest);

        await Assert.That((await c.Store.ListMembershipsByOrgAsync(c.OrgId)).Count).IsEqualTo(0);
    }

    [Test]
    public async Task EnrollMatchesExactDomainOnlyAndIgnoresCase()
    {
        var c = await ArrangeAsync("exact");
        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/organizations/{c.OrgId}/domains", new { domain = "acme.com" }, HttpStatusCode.OK);

        // Subdomains and lookalike suffixes are NOT the enrolled domain.
        var subdomain = await CreateUserAsync(c, "sub@corp.acme.com");
        var lookalike = await CreateUserAsync(c, "look@notacme.com");
        var mismatched = await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/domains/acme.com/enroll", new { user_id = subdomain.Id }, HttpStatusCode.BadRequest);
        await Assert.That(mismatched.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("email_domain_mismatch");
        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/domains/acme.com/enroll", new { user_id = lookalike.Id }, HttpStatusCode.BadRequest);

        // The route domain is matched case-insensitively.
        var member = await CreateUserAsync(c, "member@acme.com");
        var enrolled = await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/domains/ACME.COM/enroll", new { user_id = member.Id }, HttpStatusCode.OK);
        await Assert.That(enrolled.GetProperty("org_id").GetString()).IsEqualTo(c.OrgId);

        var memberships = await c.Store.ListMembershipsByOrgAsync(c.OrgId);
        await Assert.That(memberships.Count).IsEqualTo(1);
        await Assert.That(memberships.Single().UserId).IsEqualTo(member.Id);
    }

    [Test]
    public async Task ConcurrentDuplicateDomainCreateYieldsExactlyOneWinner()
    {
        var c = await ArrangeAsync("race");
        var other = await ArrangeAsync("race-other");
        var domain = $"race-{Guid.NewGuid().ToString("N")[..6]}.example";

        var responses = await Task.WhenAll(
            c.Http.SendAsync(Request(c, HttpMethod.Post, $"/v1/{c.Slug}/organizations/{c.OrgId}/domains", new { domain })),
            c.Http.SendAsync(Request(c, HttpMethod.Post, $"/v1/{c.Slug}/organizations/{c.OrgId}/domains", new { domain })));
        var statuses = responses.Select(r => r.StatusCode).ToList();
        await Assert.That(statuses.Count(s => s == HttpStatusCode.OK)).IsEqualTo(1);
        await Assert.That(statuses.Count(s => s == HttpStatusCode.Conflict)).IsEqualTo(1);
        await Assert.That((await c.Store.ListOrgDomainsAsync(c.InstanceId)).Count(d => d.Domain == domain)).IsEqualTo(1);

        // The claim is scoped per instance: the same domain may enroll in another instance's org.
        await SendAsync(other, HttpMethod.Post, $"/v1/{other.Slug}/organizations/{other.OrgId}/domains", new { domain }, HttpStatusCode.OK);
    }

    [Test]
    public async Task SignUpAutoJoinsByExactDomainMatch()
    {
        var c = await ArrangeAsync("signup");
        await SendAsync(c, HttpMethod.Post, $"/v1/{c.Slug}/organizations/{c.OrgId}/domains", new { domain = "acme.com" }, HttpStatusCode.OK);

        // Matching domain (mixed case, normalized on store) auto-joins with the member role.
        await SignUpAsync(c, "Newbie@ACME.com");
        // Subdomain and unrelated domains must not sneak in.
        await SignUpAsync(c, "sneaky@corp.acme.com");
        await SignUpAsync(c, "outsider@elsewhere.example");

        var memberships = await c.Store.ListMembershipsByOrgAsync(c.OrgId);
        await Assert.That(memberships.Count).IsEqualTo(1);
        var joined = memberships.Single();
        var newbie = await c.Store.FindUserByEmailAsync(c.InstanceId, "newbie@acme.com");
        await Assert.That(newbie).IsNotNull();
        await Assert.That(joined.UserId).IsEqualTo(newbie!.Id);
        await Assert.That(joined.Role).IsEqualTo("member");

        var audited = (await c.Store.ListAuditEventsAsync(c.InstanceId, 100)).Single(e => e.Type == "organization.enrolled");
        await Assert.That(audited.Data.TryGetValue("organization_id", out var orgId) && orgId.ToString() == c.OrgId).IsTrue();
        await Assert.That(audited.Data.TryGetValue("user_id", out var userId) && userId.ToString() == newbie.Id).IsTrue();
    }

    private static async Task SignUpAsync(Context c, string email)
    {
        using var response = await c.Http.PostAsync($"/ui/{c.Slug}/sign-up", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = "password123",
            ["name"] = "Auto Join",
        }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Found);
    }
}
