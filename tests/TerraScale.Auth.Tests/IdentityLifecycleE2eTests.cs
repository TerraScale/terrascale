namespace TerraScale.Auth.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Mfa;
using TerraScale.Auth.Store;

/// <summary>One identity walked end to end through the real HTTP surface: hosted
/// sign-up, membership role change, session use, MFA bind, revocation, SCIM
/// deprovision, deletion purge — with a tenant-isolation probe at every stage
/// and a sealed, tenant-scoped audit event required for every privileged step.
/// The individual stages have focused tests elsewhere; this class wires the
/// cross-cutting arc a single real identity must survive.</summary>
[NotInParallel]
public sealed class IdentityLifecycleE2eTests
{
    private const string Password = "correct horse battery staple";

    [Test]
    public async Task OneIdentityWalksJoinRoleSessionMfaRevocationDeprovisionAndPurgeWithAuditAndTenantIsolation()
    {
        var service = await AuthServiceFixture.GetAsync();
        var http = service.Http;
        var store = service.Store;
        var (instanceA, slugA, keyA) = await service.CreateInstanceAsync("life-a-" + Guid.NewGuid().ToString("N")[..8]);
        var (instanceB, slugB, keyB) = await service.CreateInstanceAsync("life-b-" + Guid.NewGuid().ToString("N")[..8]);

        // ---- stage 1: join through the hosted sign-up ------------------------------------
        var email = $"life-{Guid.NewGuid().ToString("N")[..8]}@example.com";
        string firstCookie;
        using (var signUp = await http.PostAsync($"/ui/{slugA}/sign-up",
            Form(("email", email), ("password", Password), ("name", "Lifecycle Probe"))))
        {
            await Assert.That(signUp.StatusCode).IsEqualTo(HttpStatusCode.Found);
            firstCookie = Cookie(signUp)!;
        }
        var user = (await store.FindUserByEmailAsync(instanceA, email))!;
        // Joining claimed the instance's one quota seat.
        var seated = await store.ReconcileQuotaSeatsAsync(instanceA);
        await Assert.That(seated.ClaimsExamined).IsEqualTo(1);
        await Assert.That(seated.ClaimsRetained).IsEqualTo(1);

        // Tenant B cannot see the identity, cannot authenticate it, and holds no trace of it.
        using (var foreignRead = await http.SendAsync(Api(HttpMethod.Get, $"/v1/{slugB}/users/{user.Id}", keyB, null)))
            await Assert.That(foreignRead.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using (var foreignSignIn = await http.PostAsync($"/ui/{slugB}/sign-in", Form(("email", email), ("password", Password))))
        {
            await Assert.That(foreignSignIn.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await foreignSignIn.Content.ReadAsStringAsync()).Contains("Invalid email or password");
            await Assert.That(foreignSignIn.Headers.Contains("Set-Cookie")).IsFalse();
        }

        // ---- stage 2: role/scope change through the management API -----------------------
        string orgId, membershipId;
        using (var org = await http.SendAsync(Api(HttpMethod.Post, $"/v1/{slugA}/organizations", keyA, Json(new { name = "Lifecycle Org" }))))
        {
            await Assert.That(org.StatusCode).IsEqualTo(HttpStatusCode.OK);
            orgId = (await org.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        }
        using (var member = await http.SendAsync(Api(HttpMethod.Post, $"/v1/{slugA}/organizations/{orgId}/memberships", keyA,
            Json(new { user_id = user.Id, role = "member" }))))
        {
            await Assert.That(member.StatusCode).IsEqualTo(HttpStatusCode.OK);
            membershipId = (await member.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        }
        using (var promoted = await http.SendAsync(Api(HttpMethod.Patch, $"/v1/{slugA}/organizations/{orgId}/memberships/{membershipId}", keyA,
            Json(new { role = "admin" }))))
        {
            await Assert.That(promoted.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await promoted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("role").GetString()).IsEqualTo("admin");
        }

        // Tenant B's key cannot touch the membership: A's org is invisible to B.
        using (var foreignPromote = await http.SendAsync(Api(HttpMethod.Patch, $"/v1/{slugB}/organizations/{orgId}/memberships/{membershipId}", keyB,
            Json(new { role = "attacker" }))))
            await Assert.That(foreignPromote.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // ---- stage 3: session create and use ----------------------------------------------
        string sessionCookie;
        using (var signIn = await http.PostAsync($"/ui/{slugA}/sign-in", Form(("email", email), ("password", Password))))
        {
            await Assert.That(signIn.StatusCode).IsEqualTo(HttpStatusCode.Found);
            sessionCookie = Cookie(signIn)!;
        }
        using (var account = await http.SendAsync(Request(HttpMethod.Get, $"/ui/{slugA}/account", sessionCookie)))
            await Assert.That(account.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // A's session cookie is worthless on B's surfaces.
        using (var foreignAccount = await http.SendAsync(Request(HttpMethod.Get, $"/ui/{slugB}/account", sessionCookie)))
        {
            await Assert.That(foreignAccount.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            await Assert.That(foreignAccount.Headers.Location!.ToString()).Contains("sign-in");
        }

        // ---- stage 4: MFA bind (TOTP) ------------------------------------------------------
        using (var start = await http.SendAsync(Request(HttpMethod.Post, $"/ui/{slugA}/mfa/enroll/start", firstCookie)))
            await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var totp = (await store.ListMfaFactorsAsync(user.Id)).Single(f => f.Type == "totp");
        var secret = SecretBox.Decrypt(AuthServiceFixture.MasterKey, totp.SecretBox!);
        using (var confirm = await http.SendAsync(Request(HttpMethod.Post,
            $"/ui/{slugA}/mfa/enroll/confirm?factor={totp.Id}", firstCookie, Form(("code", Totp.Compute(secret, DateTimeOffset.UtcNow))))))
            await Assert.That(confirm.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Tenant B cannot even list the factors of A's identity.
        using (var foreignFactors = await http.SendAsync(Api(HttpMethod.Get, $"/v1/{slugB}/users/{user.Id}/mfa-factors", keyB, null)))
            await Assert.That(foreignFactors.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // ---- stage 5: revocation ------------------------------------------------------------
        // The privileged action must fail closed on B first, then succeed on A.
        using (var foreignRevoke = await http.SendAsync(Request(HttpMethod.Post, $"/ui/{slugB}/account/sessions/revoke-all", sessionCookie)))
        {
            await Assert.That(foreignRevoke.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            await Assert.That(foreignRevoke.Headers.Location!.ToString()).Contains("sign-in");
        }
        using (var revokeAll = await http.SendAsync(Request(HttpMethod.Post, $"/ui/{slugA}/account/sessions/revoke-all", sessionCookie)))
        {
            await Assert.That(revokeAll.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            await Assert.That(revokeAll.Headers.Location!.ToString()).Contains("sign-in");
        }
        await Assert.That((await store.ListSessionsByUserAsync(user.Id)).All(s => s.Status == "revoked")).IsTrue();

        // ---- stage 6: SCIM deprovision ------------------------------------------------------
        string directoryA, tokenA, directoryB, tokenB;
        using (var created = await http.SendAsync(Api(HttpMethod.Post, $"/v1/{slugA}/directories", keyA, Json(new { }))))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            var body = await created.Content.ReadFromJsonAsync<JsonElement>();
            directoryA = body.GetProperty("id").GetString()!;
            tokenA = body.GetProperty("token").GetString()!;
        }
        using (var foreignDirectory = await http.SendAsync(Api(HttpMethod.Post, $"/v1/{slugB}/directories", keyB, Json(new { }))))
        {
            await Assert.That(foreignDirectory.StatusCode).IsEqualTo(HttpStatusCode.Created);
            var body = await foreignDirectory.Content.ReadFromJsonAsync<JsonElement>();
            directoryB = body.GetProperty("id").GetString()!;
            tokenB = body.GetProperty("token").GetString()!;
        }

        // B's directory cannot deprovision A's identity.
        using (var foreignDeprovision = await http.SendAsync(Api(HttpMethod.Patch, $"/scim/{directoryB}/Users/{user.Id}", tokenB, Json(new
        {
            Operations = new object[] { new { op = "replace", path = "active", value = false } },
        }))))
            await Assert.That(foreignDeprovision.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        using (var deprovision = await http.SendAsync(Api(HttpMethod.Patch, $"/scim/{directoryA}/Users/{user.Id}", tokenA, Json(new
        {
            Operations = new object[] { new { op = "replace", path = "active", value = false } },
        }))))
        {
            await Assert.That(deprovision.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(deprovision.Content.Headers.ContentType!.ToString()).IsEqualTo("application/scim+json");
        }
        var banned = (await store.GetUserAsync(user.Id))!;
        await Assert.That(banned.Banned).IsTrue();
        await Assert.That(banned.EmailKey).IsNull();
        await Assert.That(await store.FindUserByEmailAsync(instanceA, email)).IsNull();
        await Assert.That((await store.ListMembershipsByOrgAsync(orgId)).Any(m => m.UserId == user.Id)).IsFalse();

        // ---- stage 7: deletion purge --------------------------------------------------------
        // B cannot delete the identity; A's purge then removes every artifact.
        using (var foreignDelete = await http.SendAsync(Api(HttpMethod.Delete, $"/v1/{slugB}/users/{user.Id}", keyB, null)))
            await Assert.That(foreignDelete.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using (var deleted = await http.SendAsync(Api(HttpMethod.Delete, $"/v1/{slugA}/users/{user.Id}", keyA, null)))
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        await Assert.That((await store.GetUserAsync(user.Id))!.Deleted).IsTrue();
        await Assert.That(await store.ListSessionsByUserAsync(user.Id)).IsEmpty();
        await Assert.That(await store.ListMfaFactorsAsync(user.Id)).IsEmpty();
        await Assert.That(await store.ListUserVerificationTokensAsync(user.Id, "verify_email")).IsEmpty();
        // The quota seat the join claimed is back on the shelf.
        var released = await store.ReconcileQuotaSeatsAsync(instanceA);
        await Assert.That(released.ClaimsExamined).IsEqualTo(0);

        // ---- stage 8: the audit trail for the whole arc -------------------------------------
        var expected = new[]
        {
            "user.signed_up", "membership.created", "membership.updated", "user.signed_in",
            "mfa.enrolled", "user.signed_out", "directory.user_deprovisioned", "user.deleted",
        };
        var events = (await store.ListAuditEventsAsync(instanceA, 500)).ToList();
        foreach (var type in expected)
        {
            // Session establishment at sign-up and at the dedicated sign-in both emit
            // user.signed_in; every other stage emits exactly one event.
            var expectedCount = type == "user.signed_in" ? 2 : 1;
            var trail = events.Where(e => e.Type == type && References(e, user.Id)).ToList();
            await Assert.That(trail.Count).IsEqualTo(expectedCount).Because($"expected exactly {expectedCount} '{type}' audit events for the identity");
            var trailEvent = trail[0];
            await Assert.That(trailEvent.InstanceId).IsEqualTo(instanceA);
            await Assert.That(trailEvent.HasAuthenticIntegrity(new[] { AuthServiceFixture.AuditSigningKey, AuthServiceFixture.AuditPreviousSigningKey }))
                .IsTrue().Because($"audit event '{type}' must carry a valid integrity hash and HMAC seal");
        }

        // Tenant B's audit trail carries no trace of the identity or the org.
        foreach (var foreignEvent in await store.ListAuditEventsAsync(instanceB, 500))
        {
            await Assert.That(References(foreignEvent, user.Id)).IsFalse();
            await Assert.That(References(foreignEvent, orgId)).IsFalse();
            await Assert.That(References(foreignEvent, email)).IsFalse();
        }
    }

    /// <summary>True when the event's actor or any data value mentions the marker.</summary>
    private static bool References(AuditEvent trailEvent, string marker) =>
        trailEvent.ActorId == marker
        || trailEvent.Data.Values.Any(value => value.ToString() == marker
            || (value is JsonElement element && element.ValueKind == JsonValueKind.String && element.GetString() == marker));

    private static FormUrlEncodedContent Form(params (string Name, string Value)[] fields) =>
        new(fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value)));

    private static JsonContent Json(object body) => JsonContent.Create(body);

    private static string? Cookie(HttpResponseMessage response) =>
        (response.Headers.TryGetValues("Set-Cookie", out var values) ? values.FirstOrDefault() : null)?.Split(';')[0];

    private static HttpRequestMessage Request(HttpMethod method, string path, string cookie, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return request;
    }

    private static HttpRequestMessage Api(HttpMethod method, string path, string bearer, HttpContent? content) => new(method, path)
    {
        Content = content,
        Headers = { Authorization = new AuthenticationHeaderValue("Bearer", bearer) },
    };
}
