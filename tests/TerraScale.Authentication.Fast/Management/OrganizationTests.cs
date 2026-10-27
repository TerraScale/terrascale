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
    public async Task Project_lists_use_the_fresh_inherited_role_after_an_admin_is_demoted()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, ownerToken) = await oidc.SignInUserAsync("owner@example.test");
        var (member, memberToken) = await oidc.SignInUserAsync("member@example.test");
        var organization = await oidc.CreateOrganizationAsync(ownerToken);
        await oidc.AddMemberAsync(organization, member.Id, "admin");
        var project = await oidc.CreateProjectAsync(ownerToken, organization, "Production");
        var path = $"{Organizations}/{organization}/projects";
        await oidc.OkAsync(HttpMethod.Put, $"{path}/{project}/members/{member.Id}", ownerToken, new { role = "member" });

        foreach (var token in new[] { ownerToken, memberToken })
        {
            var page = await oidc.OkAsync(HttpMethod.Get, path, token);
            await Assert.That(page.GetProperty("items")[0].GetProperty("role").GetString()).IsEqualTo("admin");
        }

        await oidc.OkAsync(HttpMethod.Put, $"{Organizations}/{organization}/members/{member.Id}", ownerToken, new { role = "viewer" });
        var demoted = await oidc.OkAsync(HttpMethod.Get, path, memberToken);
        await Assert.That(demoted.GetProperty("items")[0].GetProperty("role").GetString()).IsEqualTo("member");
        await oidc.OkAsync(HttpMethod.Delete, $"{path}/{project}/members/{member.Id}", ownerToken, status: HttpStatusCode.NoContent);
        var inherited = await oidc.OkAsync(HttpMethod.Get, path, memberToken);
        await Assert.That(inherited.GetProperty("items")[0].GetProperty("role").GetString()).IsEqualTo("viewer");
    }

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
    public async Task Signing_out_everywhere_closes_the_api_to_every_token_issued_before()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (user, token) = await oidc.SignInUserAsync("alice@example.test");
        await oidc.OkAsync(HttpMethod.Get, Organizations, token);

        // Sign-out everywhere and a password reset both advance the epoch the token carries.
        await Assert.That(await oidc.Server.App.Services.GetRequiredService<TerraScale.Authentication.Features.Identity.Sessions>().RevokeAllAsync("terrascale", user.Id)).IsTrue();
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, Organizations, token)).IsEqualTo((401, "unauthorized"));

        clock.Advance(HostedAuth.MailCooldown);
        var session = await oidc.SignInAsync("alice@example.test");
        var (verifier, challenge) = OidcHarness.Pkce();
        using var redeemed = await oidc.RedeemCodeAsync(await oidc.CodeAsync(session, challenge), verifier);
        var fresh = (await OidcHarness.JsonAsync(redeemed)).GetProperty("access_token").GetString()!;
        await oidc.OkAsync(HttpMethod.Get, Organizations, fresh);
    }

    [Test]
    public async Task An_access_token_whose_entry_is_revoked_is_refused_while_its_sign_in_stays()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, token) = await oidc.SignInUserAsync("alice@example.test");
        await oidc.OkAsync(HttpMethod.Get, Organizations, token);

        // The token names its own entry (oi_tkn_id) besides its sign-in (oi_au_id); revoking only the entry closes it.
        var claims = JsonSerializer.Deserialize<JsonElement>(Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Decode(token.Split('.')[1]));
        await using var scope = oidc.Server.App.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<OpenIddict.Abstractions.IOpenIddictTokenManager>();
        var entry = await tokens.FindByIdAsync(claims.GetProperty("oi_tkn_id").GetString()!);
        await Assert.That(await tokens.TryRevokeAsync(entry!)).IsTrue();
        var authorizations = scope.ServiceProvider.GetRequiredService<OpenIddict.Abstractions.IOpenIddictAuthorizationManager>();
        var authorization = await authorizations.FindByIdAsync(claims.GetProperty("oi_au_id").GetString()!);
        await Assert.That(await authorizations.HasStatusAsync(authorization!, OpenIddict.Abstractions.OpenIddictConstants.Statuses.Valid)).IsTrue();

        await Assert.That(await oidc.FailAsync(HttpMethod.Get, Organizations, token)).IsEqualTo((401, "unauthorized"));
    }

    [Test]
    public async Task A_sign_in_without_a_refresh_token_ends_when_its_access_token_is_revoked()
    {
        // No offline_access: no refresh token, and only the access token's own entry to revoke.
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync("erin@example.test");
        var session = await oidc.SignInAsync("erin@example.test");
        var (verifier, challenge) = OidcHarness.Pkce();
        using var authorize = await oidc.AuthorizeAsync(OidcHarness.AuthorizePath(challenge, scope: "openid email"), session);
        using var granted = await oidc.RedeemCodeAsync(OidcHarness.Query(authorize.Headers.Location!)["code"]!, verifier);
        await Assert.That(granted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var tokens = await OidcHarness.JsonAsync(granted);
        await Assert.That(tokens.TryGetProperty("refresh_token", out _)).IsFalse();
        var access = tokens.GetProperty("access_token").GetString()!;
        await oidc.OkAsync(HttpMethod.Get, Organizations, access);

        using (var revoked = await oidc.RevokeAsync(access, "access_token"))
        {
            await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        await Assert.That(await oidc.FailAsync(HttpMethod.Get, Organizations, access)).IsEqualTo((401, "unauthorized"));
    }

    [Test]
    public async Task A_sign_in_revoked_on_one_replica_is_refused_at_once_on_another()
    {
        // Each replica reads the sign-in from TerraBase on every call; nothing it saw earlier is reused.
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await using var replica = await oidc.StartReplicaAsync();
        var (_, _, tokens) = await oidc.SignInAndRedeemAsync("alice@example.test");
        var access = tokens.GetProperty("access_token").GetString()!;
        await oidc.OkAsync(HttpMethod.Get, Organizations, access);
        await oidc.OkAsync(HttpMethod.Get, Organizations, access);

        using (var revoke = new HttpRequestMessage(HttpMethod.Post, "/terrascale/oauth/revoke")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = tokens.GetProperty("refresh_token").GetString()!, ["token_type_hint"] = "refresh_token" }),
        })
        {
            revoke.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"01M4EQ2FRJ8S8B29E8TQVWNW4J:{OidcHarness.ClientSecret}")));
            using var revoked = await replica.Client.SendAsync(revoke);
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

    [Test]
    public async Task Lists_page_in_id_order()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, token) = await oidc.SignInUserAsync("alice@example.test");
        var created = new List<string>();
        for (var index = 0; index < 3; index++)
        {
            created.Add(await oidc.CreateOrganizationAsync(token, $"Org {index}"));
        }

        var seen = new List<string>();
        string? after = null;
        do
        {
            var page = await oidc.OkAsync(HttpMethod.Get, $"{Organizations}?limit=2{(after is null ? string.Empty : $"&after={Uri.EscapeDataString(after)}")}", token);
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!));
            after = page.GetProperty("next").GetString();
        }
        while (after is not null);

        await Assert.That(seen.SequenceEqual(created.Order(StringComparer.Ordinal))).IsTrue();
        foreach (var limit in new[] { "0", "101", "x", "-1" })
        {
            await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}?limit={limit}", token)).IsEqualTo((400, "invalid_request"));
        }

        // A cursor is TerraBase's, bound to its list: another list's or a forged one is refused.
        var members = await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{created[0]}/members?limit=1", token);
        await Assert.That(members.GetProperty("next").ValueKind).IsEqualTo(JsonValueKind.Null);
        var first = (await oidc.OkAsync(HttpMethod.Get, $"{Organizations}?limit=1", token)).GetProperty("next").GetString()!;
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}/{created[0]}/projects?after={Uri.EscapeDataString(first)}", token)).IsEqualTo((400, "invalid_request"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}?after=forged", token)).IsEqualTo((400, "invalid_request"));
    }

    [Test]
    public async Task A_change_paused_before_its_commit_loses_to_what_happened_meanwhile()
    {
        // The primary's TerraBase client holds the next transaction after every read of the change; a replica commits the
        // competing change; then the held transaction goes through. It must fail and be decided again on fresh reads.
        var pause = new PausingTransactions { InnerHandler = TerraBaseFixture.NewHandler() };
        await using var oidc = await OidcHarness.StartAsync(fixture, services: services => services.AddSingleton(new HttpClient(pause)));
        await using var replica = await oidc.StartReplicaAsync();
        var (_, aliceToken) = await oidc.SignInUserAsync("alice@example.test");
        var (carol, carolToken) = await oidc.SignInUserAsync("carol@example.test");
        var (bob, bobToken) = await oidc.SignInUserAsync("bob@example.test");
        var id = await oidc.CreateOrganizationAsync(aliceToken);
        await oidc.AddMemberAsync(id, carol.Id, "admin");
        await oidc.AddMemberAsync(id, bob.Id, "member");
        var project = await oidc.CreateProjectAsync(aliceToken, id, "Production");

        // A caller demoted after its check cannot finish with its old authority.
        var rename = await pause.HoldNextAsync(() => oidc.FailAsync(HttpMethod.Patch, $"{Organizations}/{id}", carolToken, new { name = "Carol's" }));
        await OnReplicaAsync(oidc, replica, HttpMethod.Put, $"{Organizations}/{id}/members/{carol.Id}/role", aliceToken, new { role = "viewer" });
        await Assert.That(await pause.ReleaseAsync(rename)).IsEqualTo((403, "forbidden_role"));
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{id}", aliceToken)).GetProperty("name").GetString()).IsEqualTo("Acme");

        // Two owners leaving at once: the one held loses, so one owner stays.
        await OnReplicaAsync(oidc, replica, HttpMethod.Put, $"{Organizations}/{id}/members/{carol.Id}/role", aliceToken, new { role = "owner" });
        var demotion = await pause.HoldNextAsync(() => oidc.FailAsync(HttpMethod.Put, $"{Organizations}/{id}/members/{carol.Id}/role", carolToken, new { role = "admin" }));
        await Assert.That((await OnReplicaAsync(oidc, replica, HttpMethod.Post, $"{Organizations}/{id}/leave", aliceToken)).Status).IsEqualTo(204);
        await Assert.That(await pause.ReleaseAsync(demotion)).IsEqualTo((409, "last_owner"));
        var owners = (await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{id}/members", carolToken)).GetProperty("items").EnumerateArray()
            .Where(member => member.GetProperty("role").GetString() == "owner").Select(member => member.GetProperty("user_id").GetString()!).ToArray();
        await Assert.That(owners).IsEquivalentTo([carol.Id]);

        // A removal that read the member's grants before a new grant committed is decided again and removes that one too.
        var removal = await pause.HoldNextAsync(() => oidc.FailAsync(HttpMethod.Delete, $"{Organizations}/{id}/members/{bob.Id}", carolToken));
        await Assert.That((await OnReplicaAsync(oidc, replica, HttpMethod.Put, $"{Organizations}/{id}/projects/{project}/members/{bob.Id}", carolToken, new { role = "admin" })).Status).IsEqualTo(200);
        await Assert.That((await pause.ReleaseAsync(removal)).Status).IsEqualTo(204);
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}/{id}/projects/{project}", bobToken)).IsEqualTo((404, "not_found"));
        var grants = (await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{id}/projects/{project}/members", carolToken)).GetProperty("items");
        await Assert.That(grants.EnumerateArray().Any(member => member.GetProperty("user_id").GetString() == bob.Id)).IsFalse();
    }

    [Test]
    public async Task A_role_change_paused_while_its_organization_is_deleted_changes_nothing()
    {
        var pause = new PausingTransactions { InnerHandler = TerraBaseFixture.NewHandler() };
        await using var oidc = await OidcHarness.StartAsync(fixture, services: services => services.AddSingleton(new HttpClient(pause)));
        var (_, aliceToken) = await oidc.SignInUserAsync("alice@example.test");
        var (bob, _) = await oidc.SignInUserAsync("bob@example.test");
        var id = await oidc.CreateOrganizationAsync(aliceToken);
        await oidc.AddMemberAsync(id, bob.Id, "viewer");
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();

        // The deletion commits after the change's reads and before its memberships are cleaned up.
        var change = await pause.HoldNextAsync(() => oidc.FailAsync(HttpMethod.Put, $"{Organizations}/{id}/members/{bob.Id}/role", aliceToken, new { role = "member" }));
        var organization = (await store.GetAsync(AuthStore.OrganizationCollection, "terrascale", id))!;
        await Assert.That(await store.TransactAsync([AuthStore.Delete(AuthStore.OrganizationCollection, "terrascale", id, organization.Version)])).IsNull();
        await Assert.That(await pause.ReleaseAsync(change)).IsEqualTo((404, "not_found"));
        await Assert.That((await store.GetAsync(AuthStore.MembershipCollection, "terrascale", $"{id}/{bob.Id}"))!.Document.GetProperty("role").GetString()).IsEqualTo("viewer");
    }

    [Test]
    public async Task Every_grant_rewrites_the_membership_that_a_removal_deletes()
    {
        // A removal deletes the membership at the version it read with the grants it found; a grant made after that
        // read, even on another project of a collaborator, rewrites the membership, so the removal fails and retries.
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, aliceToken) = await oidc.SignInUserAsync("alice@example.test");
        var (dave, daveToken) = await oidc.SignInUserAsync("dave@example.test");
        var id = await oidc.CreateOrganizationAsync(aliceToken);
        var first = await oidc.CreateProjectAsync(aliceToken, id, "Production");
        var second = await oidc.CreateProjectAsync(aliceToken, id, "Staging");
        await oidc.GrantAsync(first, dave.Id, "viewer");
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var before = (await store.GetAsync(AuthStore.MembershipCollection, "terrascale", $"{id}/{dave.Id}"))!.Version;

        await oidc.GrantAsync(second, dave.Id, "member");
        var after = (await store.GetAsync(AuthStore.MembershipCollection, "terrascale", $"{id}/{dave.Id}"))!.Version;
        await Assert.That(after).IsNotEqualTo(before);

        // Removed, the collaborator keeps nothing in the organization.
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{id}/members/{dave.Id}", aliceToken, status: HttpStatusCode.NoContent);
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}/{id}", daveToken)).IsEqualTo((404, "not_found"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}/{id}/projects/{second}", daveToken)).IsEqualTo((404, "not_found"));
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, Organizations, daveToken)).GetProperty("items").GetArrayLength()).IsEqualTo(0);

        // Removing a collaborator's last project role removes the collaborator.
        await oidc.GrantAsync(first, dave.Id, "viewer");
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{id}/projects/{first}/members/{dave.Id}", aliceToken, status: HttpStatusCode.NoContent);
        await Assert.That(await store.GetAsync(AuthStore.MembershipCollection, "terrascale", $"{id}/{dave.Id}")).IsNull();

        // So does deleting a collaborator's last project, and only the last one.
        await oidc.GrantAsync(first, dave.Id, "viewer");
        await oidc.GrantAsync(second, dave.Id, "viewer");
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{id}/projects/{first}", aliceToken, status: HttpStatusCode.NoContent);
        await Assert.That(await store.GetAsync(AuthStore.ProjectMemberCollection, "terrascale", $"{first}/{dave.Id}")).IsNull();
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, Organizations, daveToken)).GetProperty("items").GetArrayLength()).IsEqualTo(1);
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{id}/projects/{second}", aliceToken, status: HttpStatusCode.NoContent);
        await Assert.That(await store.GetAsync(AuthStore.MembershipCollection, "terrascale", $"{id}/{dave.Id}")).IsNull();
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, Organizations, daveToken)).GetProperty("items").GetArrayLength()).IsEqualTo(0);

        // A role left on a deleted project (its clean-up failed) does not keep a collaborator whose last live role goes.
        var third = await oidc.CreateProjectAsync(aliceToken, id, "Preview");
        var fourth = await oidc.CreateProjectAsync(aliceToken, id, "Demo");
        await oidc.GrantAsync(third, dave.Id, "viewer");
        await oidc.GrantAsync(fourth, dave.Id, "viewer");
        var leftover = (await store.GetAsync(AuthStore.ProjectMemberCollection, "terrascale", $"{third}/{dave.Id}"))!.Document;
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{id}/projects/{third}", aliceToken, status: HttpStatusCode.NoContent);
        // The project's deletion job fences the store, so the store refuses to bring the role back; a clean-up that failed
        // before the deletion leaves it behind in storage itself.
        await Assert.That(await store.TransactAsync([AuthStore.Create(AuthStore.ProjectMemberCollection, "terrascale", $"{third}/{dave.Id}", leftover)])).IsEqualTo(0);
        await oidc.Server.App.Services.GetRequiredService<TerraBaseDatabase>().PutDocumentAsync(AuthStore.ProjectMemberCollection,
            AuthStore.NaturalKeyId("terrascale", AuthStore.ProjectMemberCollection, $"{third}/{dave.Id}"), leftover, TerraBasePrecondition.Absent);
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{id}/projects/{fourth}/members/{dave.Id}", aliceToken, status: HttpStatusCode.NoContent);
        await Assert.That(await store.GetAsync(AuthStore.MembershipCollection, "terrascale", $"{id}/{dave.Id}")).IsNull();
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, Organizations, daveToken)).GetProperty("items").GetArrayLength()).IsEqualTo(0);
    }

    private static async Task<(int Status, string Body)> OnReplicaAsync(OidcHarness oidc, AuthTestServer replica, HttpMethod method, string path, string token, object? body = null)
    {
        using var response = await oidc.CallAsync(method, path, token, body, client: replica.Client);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

}
