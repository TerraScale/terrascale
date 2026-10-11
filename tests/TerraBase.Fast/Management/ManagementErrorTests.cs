using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class ManagementErrorTests
{
    [Test]
    [Arguments("POST", "/v1/management/organizations", "{}", "name must be 1 to 128 characters, include a letter or digit and have no control characters")]
    [Arguments("PUT", "/v1/management/current-organization", "{}", "organizationId is required")]
    [Arguments("PUT", "/v1/management/organizations/missing/plan", "{}", "plan must be hobby, pro or enterprise")]
    [Arguments("POST", "/v1/management/organizations/missing/projects", "{}", "name must be 1 to 128 characters, include a letter or digit and have no control characters")]
    [Arguments("POST", "/v1/management/organizations/missing/projects/missing/databases", "{\"name\":\"Main\"}", "name must be a lowercase database name of at most 63 characters")]
    [Arguments("PUT", "/v1/management/organizations/missing/members/member/role", "{}", "role must be owner, admin, member or viewer")]
    [Arguments("PUT", "/v1/management/organizations/missing/members/member/role", "{\"role\":7}", "role must be owner, admin, member or viewer")]
    [Arguments("POST", "/v1/management/organizations/missing/invitations", "{\"email\":\"invalid\"}", "email must be one address of at most 254 characters")]
    [Arguments("POST", "/v1/management/organizations/missing/ownership-transfer", "{}", "userId must be a canonical uppercase ULID")]
    [Arguments("PUT", "/v1/management/organizations/missing/audit-retention", "{\"days\":0}", "days must be an integer between 1 and 90")]
    [Arguments("PUT", "/v1/management/organizations/missing/audit-retention", "{\"days\":91}", "days must be an integer between 1 and 90")]
    [Arguments("POST", "/v1/management/invitations/accept", "{}", "token is required")]
    [Arguments("POST", "/v1/management/invitations/lookup", "{\"tokenHash\":\"invalid\"}", "tokenHash must be 64 lowercase hex characters")]
    public async Task Invalid_input_returns_an_uncacheable_error_envelope(string method, string path, string json, string message)
    {
        await using var api = await ManagementApi.StartAsync();
        path = path.Replace("/missing", "/" + Ulid.NewUlid(), StringComparison.Ordinal);
        path = path.Replace("/members/member/", "/members/" + Ulid.NewUlid() + "/", StringComparison.Ordinal);
        using var request = new HttpRequestMessage(new HttpMethod(method), Uri(path))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        await ErrorAssert.ErrorAsync(api.Client.SendAsync(request), HttpStatusCode.BadRequest, "invalid_request", message);
    }

    [Test]
    [Arguments("/v1/management/organizations?limit=0")]
    [Arguments("/v1/management/organizations/missing/projects?limit=101")]
    [Arguments("/v1/management/organizations/missing/projects/missing/databases?limit=invalid")]
    [Arguments("/v1/management/organizations/missing/members?limit=0")]
    [Arguments("/v1/management/organizations/missing/invitations?limit=101")]
    [Arguments("/v1/management/organizations/missing/audit-events?limit=invalid")]
    public async Task Invalid_pagination_returns_the_validation_case(string path)
    {
        await using var api = await ManagementApi.StartAsync();
        path = path.Replace("/missing", "/" + Ulid.NewUlid(), StringComparison.Ordinal);
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(Uri(path)), HttpStatusCode.BadRequest, "invalid_request",
            "limit must be an integer between 1 and 100");
    }

    [Test]
    public async Task Invitation_lookup_keeps_its_existing_not_found_envelope()
    {
        await using var api = await ManagementApi.StartAsync();
        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(Uri("/v1/management/invitations/lookup"), new { tokenHash = new string('0', 64) }),
            HttpStatusCode.NotFound, "not_found", "no pending invitation has this token");
    }

    [Test]
    public async Task An_expired_invitation_returns_a_conflict_without_adding_a_member()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await ManagementApi.StartAsync(time: time);
        var organization = await api.CreateOrganizationAsync("Expiry");
        var email = new Faker().Internet.Email();
        using var invitee = api.As(Ulid.NewUlid().ToString(), email);
        using var invited = await api.Client.PostAsJsonAsync(Uri($"/v1/management/organizations/{organization}/invitations"), new { email });
        invited.EnsureSuccessStatusCode();
        var token = (await invited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();

        time.Advance(TimeSpan.FromDays(8));
        await ErrorAssert.ErrorAsync(invitee.PostAsJsonAsync(Uri("/v1/management/invitations/accept"), new { token }),
            HttpStatusCode.Conflict, "expired", "the invitation expired");
        var members = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"/v1/management/organizations/{organization}/members"));
        await Assert.That(members.GetProperty("items").GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    [Arguments("", "documents:read", "name must be 1 to 100 characters")]
    [Arguments("key", "unknown", "scopes must be a non-empty subset of: documents:read, documents:write, shapes:read")]
    public async Task Invalid_operator_key_input_returns_the_validation_envelope(string name, string scope, string message)
    {
        await using var api = await ManagementApi.StartAsync();
        await ErrorAssert.ErrorAsync(api.Server.Client.PostAsJsonAsync(Uri("/admin/api-keys"), new { name, scopes = new[] { scope } }),
            HttpStatusCode.BadRequest, "invalid_request", message);
    }

    private static Uri Uri(string path) => new(path, UriKind.Relative);
}
