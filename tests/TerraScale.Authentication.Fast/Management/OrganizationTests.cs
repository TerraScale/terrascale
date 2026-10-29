using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Fast.Management;

/// <summary>
/// The platform management API (TS-598): organizations, members, projects and project roles, over real HTTP on the
/// real TerraBase, as signed-in users.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class OrganizationTests(TerraBaseFixture fixture)
{
    private const string ServiceId = "01M4EQ2FRJ8S8B29E8TQVWNW4K";
    private const string ServiceSecret = "service-test-secret-0123456789abcdefghij";

    [Test]
    public async Task Only_the_callers_own_live_sign_in_token_opens_the_api()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture, values =>
        {
            values["access-token-audiences"] = "https://api.terrascale.tech";
            values["service-clients"] = $"{ServiceId}:{ServiceSecret}";
        });
        var (_, _, tokens) = await oidc.SignInAndRedeemAsync("alice@example.test");
        var access = tokens.GetProperty("access_token").GetString()!;

        using (var anonymous = await oidc.CallAsync(HttpMethod.Get, Organizations, null))
        {
            await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(anonymous.Headers.WwwAuthenticate.Single().Scheme).IsEqualTo("Bearer");
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(anonymous)).IsEqualTo("unauthorized");
        }

        // An identity token (typ JWT), a service client's token (no issuer audience) and garbage are all refused.
        using var service = new HttpRequestMessage(HttpMethod.Post, "/terrascale/oauth/token") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }) };
        service.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ServiceId}:{ServiceSecret}")));
        using var serviceResponse = await oidc.Client.SendAsync(service);
        var serviceToken = (await OidcHarness.JsonAsync(serviceResponse)).GetProperty("access_token").GetString()!;
        foreach (var token in new[] { tokens.GetProperty("id_token").GetString()!, serviceToken, access + "x", "not-a-jwt" })
        {
            await Assert.That(await oidc.FailAsync(HttpMethod.Get, Organizations, token)).IsEqualTo((401, "unauthorized"));
        }

        var page = await oidc.OkAsync(HttpMethod.Get, Organizations, access);
        await Assert.That(page.GetProperty("items").GetArrayLength()).IsEqualTo(0);
        await Assert.That(page.GetProperty("next").ValueKind).IsEqualTo(JsonValueKind.Null);

        // Revoking the sign-in (RFC 7009) closes the API to its access token at once.
        using (var revoked = await oidc.RevokeAsync(tokens.GetProperty("refresh_token").GetString()!))
        {
            await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        await Assert.That(await oidc.FailAsync(HttpMethod.Get, Organizations, access)).IsEqualTo((401, "unauthorized"));
    }

    [Test]
    public async Task An_owner_creates_renames_and_deletes_an_organization_and_outsiders_see_nothing()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (alice, aliceToken) = await oidc.SignInUserAsync("alice@example.test");
        var (_, bobToken) = await oidc.SignInUserAsync("bob@example.test");

        using var created = await oidc.CallAsync(HttpMethod.Post, Organizations, aliceToken, new { name = "  Acme  " });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Headers.CacheControl!.NoStore).IsTrue();
        var organization = await OidcHarness.JsonAsync(created);
        var id = organization.GetProperty("id").GetString()!;
        await Assert.That(created.Headers.Location!.OriginalString).IsEqualTo($"{Organizations}/{id}");
        await Assert.That(TerraScale.DotNet.TerraScaleIds.IsValid(id)).IsTrue();
        await Assert.That(organization.GetProperty("name").GetString()).IsEqualTo("Acme");
        await Assert.That(organization.GetProperty("created_by").GetString()).IsEqualTo(alice.Id);
        await Assert.That(organization.GetProperty("role").GetString()).IsEqualTo("owner");

        var listed = (await oidc.OkAsync(HttpMethod.Get, Organizations, aliceToken)).GetProperty("items");
        await Assert.That(listed.GetArrayLength()).IsEqualTo(1);
        await Assert.That(listed[0].GetProperty("role").GetString()).IsEqualTo("owner");

        // Someone outside gets 404 for the organization and everything under it, as for an unknown id.
        foreach (var (method, path) in new[] { (HttpMethod.Get, $"{Organizations}/{id}"), (HttpMethod.Get, $"{Organizations}/{id}/members"), (HttpMethod.Get, $"{Organizations}/{id}/projects"), (HttpMethod.Delete, $"{Organizations}/{id}") })
        {
            await Assert.That(await oidc.FailAsync(method, path, bobToken)).IsEqualTo((404, "not_found"));
        }

        await Assert.That(await oidc.FailAsync(HttpMethod.Patch, $"{Organizations}/{id}", bobToken, new { name = "Mine" })).IsEqualTo((404, "not_found"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}/{Guid.CreateVersion7():D}", aliceToken)).IsEqualTo((404, "not_found"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}/{id.ToLowerInvariant()}", aliceToken)).IsEqualTo((404, "not_found"));

        // Names are validated; unknown members and other bodies are refused.
        foreach (var body in new object[] { new { name = "   " }, new { name = "--" }, new { name = "a\u0007b" }, new { name = new string('a', 129) }, new { name = "Ok", plan = "pro" }, new { other = 1 } })
        {
            await Assert.That(await oidc.FailAsync(HttpMethod.Patch, $"{Organizations}/{id}", aliceToken, body)).IsEqualTo((400, "invalid_request"));
        }

        // JSON is UTF-8: another or an unknown declared charset is refused as a bad body, never a server error.
        foreach (var charset in new[] { "not-an-encoding", "utf-16" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Patch, $"{Organizations}/{id}") { Content = new StringContent("""{"name":"Acme Cloud"}""") };
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse($"application/json; charset={charset}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", aliceToken);
            using var response = await oidc.Client.SendAsync(request);
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(response)).IsEqualTo("invalid_request");
        }

        // A valid body past 16 KiB (here, padded with whitespace) is refused, chunked or not.
        foreach (var chunked in new[] { false, true })
        {
            using var request = new HttpRequestMessage(HttpMethod.Patch, $"{Organizations}/{id}") { Content = new StringContent($"{{\"name\":\"Big\"{new string(' ', 17 * 1024)}}}", Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", aliceToken);
            request.Headers.TransferEncodingChunked = chunked;
            using var response = await oidc.Client.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }

        var renamed = await oidc.OkAsync(HttpMethod.Patch, $"{Organizations}/{id}", aliceToken, new { name = "Acme Cloud" });
        await Assert.That(renamed.GetProperty("name").GetString()).IsEqualTo("Acme Cloud");

        // An organization with projects cannot be deleted; without them it can, and it is gone.
        var project = await oidc.CreateProjectAsync(aliceToken, id, "Production");
        await Assert.That(await oidc.FailAsync(HttpMethod.Delete, $"{Organizations}/{id}", aliceToken)).IsEqualTo((409, "not_empty"));
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{id}/projects/{project}", aliceToken, status: HttpStatusCode.NoContent);
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{id}", aliceToken, status: HttpStatusCode.NoContent);
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}/{id}", aliceToken)).IsEqualTo((404, "not_found"));
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, Organizations, aliceToken)).GetProperty("items").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task Roles_gate_every_change_and_an_organization_keeps_an_owner()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (alice, aliceToken) = await oidc.SignInUserAsync("alice@example.test");
        var (bob, bobToken) = await oidc.SignInUserAsync("bob@example.test");
        var (carol, carolToken) = await oidc.SignInUserAsync("carol@example.test");
        var id = await oidc.CreateOrganizationAsync(aliceToken);
        await Assert.That(await oidc.AddMemberAsync(id, bob.Id, "viewer")).IsTrue();
        await Assert.That(await oidc.AddMemberAsync(id, carol.Id, "admin")).IsTrue();
        await Assert.That(await oidc.AddMemberAsync(id, carol.Id, "viewer")).IsFalse();

        // Members list in user id order.
        var members = (await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{id}/members", bobToken)).GetProperty("items").EnumerateArray()
            .Select(member => $"{member.GetProperty("user_id").GetString()} {member.GetProperty("email").GetString()} {member.GetProperty("role").GetString()}").ToList();
        var expected = new[] { $"{alice.Id} alice@example.test owner", $"{bob.Id} bob@example.test viewer", $"{carol.Id} carol@example.test admin" }.Order(StringComparer.Ordinal).ToList();
        await Assert.That(members.SequenceEqual(expected)).IsTrue();

        // A viewer reads but changes nothing.
        await Assert.That(await oidc.FailAsync(HttpMethod.Patch, $"{Organizations}/{id}", bobToken, new { name = "Mine" })).IsEqualTo((403, "forbidden_role"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, $"{Organizations}/{id}/projects", bobToken, new { name = "Mine" })).IsEqualTo((403, "forbidden_role"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Put, $"{Organizations}/{id}/members/{carol.Id}/role", bobToken, new { role = "viewer" })).IsEqualTo((403, "forbidden_role"));

        // An admin manages non-owners only.
        var promoted = await oidc.OkAsync(HttpMethod.Put, $"{Organizations}/{id}/members/{bob.Id}/role", carolToken, new { role = "member" });
        await Assert.That(promoted.GetProperty("role").GetString()).IsEqualTo("member");
        await Assert.That(promoted.GetProperty("email").GetString()).IsEqualTo("bob@example.test");
        await Assert.That(await oidc.FailAsync(HttpMethod.Put, $"{Organizations}/{id}/members/{bob.Id}/role", carolToken, new { role = "owner" })).IsEqualTo((403, "forbidden_role"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Put, $"{Organizations}/{id}/members/{alice.Id}/role", carolToken, new { role = "admin" })).IsEqualTo((403, "forbidden_role"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Delete, $"{Organizations}/{id}/members/{alice.Id}", carolToken)).IsEqualTo((403, "forbidden_role"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Put, $"{Organizations}/{id}/members/{bob.Id}/role", carolToken, new { role = "superuser" })).IsEqualTo((400, "invalid_request"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Put, $"{Organizations}/{id}/members/{Guid.NewGuid():D}/role", carolToken, new { role = "viewer" })).IsEqualTo((404, "not_found"));

        // The last owner can neither be demoted nor leave; with a second owner it can.
        await Assert.That(await oidc.FailAsync(HttpMethod.Put, $"{Organizations}/{id}/members/{alice.Id}/role", aliceToken, new { role = "admin" })).IsEqualTo((409, "last_owner"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, $"{Organizations}/{id}/leave", aliceToken)).IsEqualTo((409, "last_owner"));
        await oidc.OkAsync(HttpMethod.Put, $"{Organizations}/{id}/members/{carol.Id}/role", aliceToken, new { role = "owner" });
        await oidc.OkAsync(HttpMethod.Put, $"{Organizations}/{id}/members/{alice.Id}/role", aliceToken, new { role = "admin" });
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, $"{Organizations}/{id}/leave", carolToken)).IsEqualTo((409, "last_owner"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Delete, $"{Organizations}/{id}", aliceToken)).IsEqualTo((403, "forbidden_role"));

        // Removal ends access at once; leaving too.
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{id}/members/{bob.Id}", aliceToken, status: HttpStatusCode.NoContent);
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}/{id}", bobToken)).IsEqualTo((404, "not_found"));
        await oidc.OkAsync(HttpMethod.Post, $"{Organizations}/{id}/leave", aliceToken, status: HttpStatusCode.NoContent);
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}/{id}", aliceToken)).IsEqualTo((404, "not_found"));
        var remaining = (await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{id}/members", carolToken)).GetProperty("items");
        await Assert.That(remaining.EnumerateArray().Select(member => member.GetProperty("user_id").GetString()!).ToArray()).IsEquivalentTo([carol.Id]);
    }

    [Test]
    public async Task Project_roles_only_add_access_and_collaborators_see_only_their_projects()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (alice, aliceToken) = await oidc.SignInUserAsync("alice@example.test");
        var (bob, bobToken) = await oidc.SignInUserAsync("bob@example.test");
        var (dave, daveToken) = await oidc.SignInUserAsync("dave@example.test");
        var id = await oidc.CreateOrganizationAsync(aliceToken);
        await oidc.AddMemberAsync(id, bob.Id, "viewer");

        using var created = await oidc.CallAsync(HttpMethod.Post, $"{Organizations}/{id}/projects", aliceToken, new { name = "Production" });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var production = await OidcHarness.JsonAsync(created);
        var first = production.GetProperty("id").GetString()!;
        // Its tenant's issuer, on the same origin as the platform's.
        await Assert.That(production.GetProperty("issuer").GetString()).IsEqualTo($"{OidcHarness.PublicOrigin}/{first}");
        await Assert.That(production.GetProperty("organization_id").GetString()).IsEqualTo(id);
        await Assert.That(production.GetProperty("role").GetString()).IsEqualTo("admin");
        var second = await oidc.CreateProjectAsync(aliceToken, id, "Staging");

        // Names are unique in the organization, ignoring case, on create and rename.
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, $"{Organizations}/{id}/projects", aliceToken, new { name = "PRODUCTION" })).IsEqualTo((409, "name_taken"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Patch, $"{Organizations}/{id}/projects/{second}", aliceToken, new { name = "production" })).IsEqualTo((409, "name_taken"));
        // Ignoring case means what ordinal ignore-case compares: Greek final sigma included.
        var greek = await oidc.CreateProjectAsync(aliceToken, id, "ας");
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, $"{Organizations}/{id}/projects", aliceToken, new { name = "ΑΣ" })).IsEqualTo((409, "name_taken"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, $"{Organizations}/{id}/projects", aliceToken, new { name = "ασ" })).IsEqualTo((409, "name_taken"));
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{id}/projects/{greek}", aliceToken, status: HttpStatusCode.NoContent);

        // A rename releases the old name.
        await oidc.OkAsync(HttpMethod.Patch, $"{Organizations}/{id}/projects/{first}", aliceToken, new { name = "Prod" });
        var reused = await oidc.CreateProjectAsync(aliceToken, id, "Production");
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{id}/projects/{reused}", aliceToken, status: HttpStatusCode.NoContent);

        // An organization viewer is a viewer of every project until a project role raises it.
        var viewed = (await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{id}/projects", bobToken)).GetProperty("items");
        await Assert.That(viewed.EnumerateArray().Select(project => (project.GetProperty("id").GetString(), project.GetProperty("role").GetString())).ToArray())
            .IsEquivalentTo(new (string?, string?)[] { (first, "viewer"), (second, "viewer") });
        await Assert.That(await oidc.FailAsync(HttpMethod.Patch, $"{Organizations}/{id}/projects/{first}", bobToken, new { name = "Mine" })).IsEqualTo((403, "forbidden_role"));
        var granted = await oidc.OkAsync(HttpMethod.Put, $"{Organizations}/{id}/projects/{first}/members/{bob.Id}", aliceToken, new { role = "admin" });
        await Assert.That(granted.GetProperty("email").GetString()).IsEqualTo("bob@example.test");
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{id}/projects/{first}", bobToken)).GetProperty("role").GetString()).IsEqualTo("admin");
        await oidc.OkAsync(HttpMethod.Patch, $"{Organizations}/{id}/projects/{first}", bobToken, new { name = "Primary" });
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{id}/projects/{second}", bobToken)).GetProperty("role").GetString()).IsEqualTo("viewer");
        await Assert.That(await oidc.FailAsync(HttpMethod.Put, $"{Organizations}/{id}/projects/{first}/members/{bob.Id}", aliceToken, new { role = "owner" })).IsEqualTo((400, "invalid_request"));

        // Direct grants need an organization member; anyone else is invited.
        await Assert.That(await oidc.FailAsync(HttpMethod.Put, $"{Organizations}/{id}/projects/{first}/members/{dave.Id}", aliceToken, new { role = "viewer" })).IsEqualTo((404, "not_found"));

        // A collaborator sees the organization (without a role) and only its project.
        await Assert.That(await oidc.GrantAsync(second, dave.Id, "member")).IsTrue();
        var organizations = (await oidc.OkAsync(HttpMethod.Get, Organizations, daveToken)).GetProperty("items");
        await Assert.That(organizations.GetArrayLength()).IsEqualTo(1);
        await Assert.That(organizations[0].GetProperty("role").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{id}", daveToken)).GetProperty("role").ValueKind).IsEqualTo(JsonValueKind.Null);
        var visible = (await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{id}/projects", daveToken)).GetProperty("items");
        await Assert.That(visible.EnumerateArray().Select(project => (project.GetProperty("id").GetString(), project.GetProperty("role").GetString())).ToArray())
            .IsEquivalentTo(new (string?, string?)[] { (second, "member") });
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}/{id}/projects/{first}", daveToken)).IsEqualTo((404, "not_found"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}/{id}/members", daveToken)).IsEqualTo((403, "forbidden_role"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, $"{Organizations}/{id}/projects", daveToken, new { name = "Mine" })).IsEqualTo((403, "forbidden_role"));
        var explicitRoles = (await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{id}/projects/{second}/members", daveToken)).GetProperty("items");
        await Assert.That(explicitRoles.EnumerateArray().Select(member => (member.GetProperty("user_id").GetString(), member.GetProperty("role").GetString())).ToArray())
            .IsEquivalentTo(new (string?, string?)[] { (alice.Id, "admin"), (dave.Id, "member") });

        // Removing a project role falls back to the implied one; leaving removes a collaborator's roles.
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{id}/projects/{first}/members/{bob.Id}", aliceToken, status: HttpStatusCode.NoContent);
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{id}/projects/{first}", bobToken)).GetProperty("role").GetString()).IsEqualTo("viewer");
        await Assert.That(await oidc.FailAsync(HttpMethod.Delete, $"{Organizations}/{id}/projects/{first}/members/{bob.Id}", aliceToken)).IsEqualTo((404, "not_found"));
        await oidc.OkAsync(HttpMethod.Post, $"{Organizations}/{id}/leave", daveToken, status: HttpStatusCode.NoContent);
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}/{id}", daveToken)).IsEqualTo((404, "not_found"));

        // Removing a member removes its project roles in the organization.
        await oidc.OkAsync(HttpMethod.Put, $"{Organizations}/{id}/projects/{second}/members/{bob.Id}", aliceToken, new { role = "admin" });
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{id}/members/{bob.Id}", aliceToken, status: HttpStatusCode.NoContent);
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}/{id}/projects/{second}", bobToken)).IsEqualTo((404, "not_found"));
        var left = (await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{id}/projects/{second}/members", aliceToken)).GetProperty("items");
        await Assert.That(left.EnumerateArray().Select(member => member.GetProperty("user_id").GetString()!).ToArray()).IsEquivalentTo([alice.Id]);
    }

    private static async Task<(int Status, string Body)> OnReplicaAsync(OidcHarness oidc, AuthTestServer replica, HttpMethod method, string path, string token, object? body = null)
    {
        using var response = await oidc.CallAsync(method, path, token, body, client: replica.Client);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

}
