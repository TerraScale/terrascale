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
}
