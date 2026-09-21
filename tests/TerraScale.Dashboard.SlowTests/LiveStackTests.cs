#if TS_AUTH_STACK
namespace TerraScale.Dashboard.SlowTests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Dashboard.Testing;

/// <summary>
/// The live stack: the dashboard in live mode against a real ts-auth identity
/// provider and real TerraBase storage, all booted in-process by
/// <see cref="LiveDashboardFixture"/>. These prove the production wiring —
/// OIDC sign-in, Terrabase-persisted account worlds, real database
/// provisioning, refresh-token revocation — end to end.
/// </summary>
[TUnit.Core.NotInParallel]
public sealed class LiveStackTests
{
    private const string Password = "Live-tests-123!";

    /// <summary>The shared running stack across every live test.</summary>
    private static readonly Task<LiveDashboardFixture> Stack = LiveDashboardFixture.Shared;

    // ---- full registration journey ---------------------------------------------------

    [Test]
    public async Task Register_creates_the_ts_auth_user_and_persists_the_world_in_terrabase()
    {
        var fixture = await Stack;
        using var factory = fixture.CreateDashboardFactory();
        using var client = fixture.CreateDashboardClient(factory);
        var (email, subject, _) = await RegisterMemberAsync(fixture, client, "Live One");

        // The user exists in ts-auth (the helper throws when the management
        // listing cannot find the registered email); the account world exists
        // in the Terrabase state database.
        var world = await fixture.StateSdk.Documents.GetAsync(
            fixture.StateDatabase, "dashboard_account_worlds", LiveDashboardFixture.WorldDocumentId(subject));
        await Assert.That(world.Data.GetProperty("email").GetString()).IsEqualTo(email);
        await Assert.That(world.Data.GetProperty("subject").GetString()).IsEqualTo(subject);

        // The wizard completed: the dashboard renders the signed-in overview.
        var overview = await GetPageAsync(client, "/");
        await Assert.That(overview.StatusCode).IsEqualTo(200);
        await Assert.That(overview.Contains("Workspace metrics")).IsTrue();
    }

    [Test]
    public async Task Sign_in_again_reaches_the_overview_and_state_survives_a_dashboard_restart()
    {
        var fixture = await Stack;
        using var firstFactory = fixture.CreateDashboardFactory();
        using var firstClient = fixture.CreateDashboardClient(firstFactory);
        var (email, _, _) = await RegisterMemberAsync(fixture, firstClient, "Live Two");

        // Rename the organization through the dashboard.
        using var rename = await PostFormAsync(firstClient, "/api/v1/dashboard/organization/rename", new Dictionary<string, string> { ["name"] = "Renamed Live Org" });
        await Assert.That(rename.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(rename.Headers.Location!.ToString()).IsEqualTo("/organization");
        var organization = await GetPageAsync(firstClient, "/organization");
        await Assert.That(organization.Contains("Renamed Live Org")).IsTrue();

        // Sign out: the dashboard session dies and the live gate 401s.
        using var logout = await PostFormAsync(firstClient, "/api/v1/auth/logout", new Dictionary<string, string>());
        await Assert.That(logout.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(logout.Headers.Location!.ToString()).IsEqualTo("/login");
        var signedOut = await GetPageAsync(firstClient, "/");
        await Assert.That(signedOut.StatusCode).IsEqualTo(401);

        // A brand-new dashboard instance (restart) sharing the same Terrabase
        // state database: the renamed organization comes back from storage.
        using var secondFactory = fixture.CreateDashboardFactory();
        using var secondClient = fixture.CreateDashboardClient(secondFactory);
        using var signIn = await PostFormAsync(secondClient, "/api/v1/auth/password/login", new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = Password,
        });
        await Assert.That(signIn.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(signIn.Headers.Location!.ToString()).IsEqualTo("/");
        var overview = await GetPageAsync(secondClient, "/");
        await Assert.That(overview.StatusCode).IsEqualTo(200);
        await Assert.That(overview.Contains("Workspace metrics")).IsTrue();
        var restored = await GetPageAsync(secondClient, "/organization");
        await Assert.That(restored.Contains("Renamed Live Org")).IsTrue();
    }

    [Test]
    public async Task Create_database_provisions_a_real_terrabase_database_and_renders_the_row()
    {
        var fixture = await Stack;
        using var factory = fixture.CreateDashboardFactory();
        using var client = fixture.CreateDashboardClient(factory);
        _ = await RegisterMemberAsync(fixture, client, "Live Three");

        var name = "live-db-" + Guid.NewGuid().ToString("N")[..10];
        using var created = await PostFormAsync(client, "/api/v1/dashboard/databases/create", new Dictionary<string, string> { ["name"] = name });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(created.Headers.Location!.ToString()).IsEqualTo("/databases");

        // The dashboard renders the row for the provisioned database.
        var databases = await GetPageAsync(client, "/databases");
        await Assert.That(databases.StatusCode).IsEqualTo(200);
        await Assert.That(databases.Contains(name)).IsTrue();

        // The database exists in Terrabase through the SDK: list and direct get.
        var listed = await fixture.StateSdk.Databases.ListAsync();
        await Assert.That(listed.Any(database => database.Name == name)).IsTrue();
        var record = await fixture.StateSdk.Databases.GetAsync(name);
        await Assert.That(record.Name).IsEqualTo(name);
        await Assert.That(record.Deleted).IsFalse();
    }

    [Test]
    public async Task Logout_revokes_the_ts_auth_refresh_token()
    {
        var fixture = await Stack;
        using var factory = fixture.CreateDashboardFactory();
        using var client = fixture.CreateDashboardClient(factory);
        var (_, _, cookie) = await RegisterMemberAsync(fixture, client, "Live Four");

        // The dashboard's opaque cookie maps to the server-held token pair.
        var sessions = factory.Services.GetRequiredService<LiveAuthSessionService>();
        var session = sessions.Lookup(cookie);
        await Assert.That(session).IsNotNull();
        var before = await IntrospectRefreshTokenAsync(fixture, session!.RefreshToken);
        await Assert.That(before.GetProperty("active").GetBoolean()).IsTrue();

        using var logout = await PostFormAsync(client, "/api/v1/auth/logout", new Dictionary<string, string>());
        await Assert.That(logout.StatusCode).IsEqualTo(HttpStatusCode.Redirect);

        // The refresh token is revoked at the identity provider.
        await Assert.That(sessions.Lookup(cookie)).IsNull();
        var after = await IntrospectRefreshTokenAsync(fixture, session.RefreshToken);
        await Assert.That(after.GetProperty("active").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task Wrong_password_is_rejected_with_the_login_error_copy()
    {
        var fixture = await Stack;
        using var factory = fixture.CreateDashboardFactory();
        using var registerClient = fixture.CreateDashboardClient(factory);
        var (email, _, _) = await RegisterMemberAsync(fixture, registerClient, "Live Five");

        using var client = fixture.CreateDashboardClient(factory);
        using var rejected = await PostFormAsync(client, "/api/v1/auth/password/login", new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = "definitely-not-the-password",
        });
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(rejected.Headers.Location!.ToString())
            .IsEqualTo("/login?error=" + Uri.EscapeDataString("Incorrect email or password."));

        var loginPage = await GetPageAsync(client, rejected.Headers.Location!.ToString());
        await Assert.That(loginPage.Contains("Incorrect email or password.")).IsTrue();

        // And the failed sign-in issued no dashboard session.
        var blocked = await GetPageAsync(client, "/");
        await Assert.That(blocked.StatusCode).IsEqualTo(401);
    }

    // ---- helpers ---------------------------------------------------------------------

    /// <summary>Registers a fresh member through the onboarding form, walks the
    /// wizard, and returns (email, ts-auth subject, dashboard session cookie).</summary>
    private static async Task<(string Email, string Subject, string SessionCookie)> RegisterMemberAsync(LiveDashboardFixture fixture, HttpClient client, string displayName)
    {
        var email = $"live-{Guid.NewGuid():N}@example.com";
        using var register = await PostFormAsync(client, "/onboarding/register", new Dictionary<string, string>
        {
            ["displayName"] = displayName,
            ["email"] = email,
            ["password"] = Password,
            ["confirmPassword"] = Password,
            ["terms"] = "true",
        });
        await Assert.That(register.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(register.Headers.Location!.ToString()).IsEqualTo("/onboarding");
        var cookie = register.Headers.GetValues("Set-Cookie").First()
            .Split(';', 2)[0]["ts_session=".Length..];

        using var payment = await PostFormAsync(client, "/onboarding/step", new Dictionary<string, string> { ["step"] = "payment-method", ["bypass"] = "1" });
        using var project = await PostFormAsync(client, "/onboarding/step", new Dictionary<string, string> { ["step"] = "project", ["name"] = "Personal", ["region"] = "GRU" });
        foreach (var response in new[] { payment, project })
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        }

        return (email, await FindSubjectByEmailAsync(fixture, email), cookie);
    }

    /// <summary>Live mode gates unsafe requests on a same-origin header; the
    /// browser always sends Origin with form posts, so every POST does too.</summary>
    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string path, IReadOnlyDictionary<string, string> fields)
    {
        using var form = new FormUrlEncodedContent(fields);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = form };
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost");
        return await client.SendAsync(request);
    }

    private static async Task<HtmlPage> GetPageAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        return HtmlPage.Capture((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> FindSubjectByEmailAsync(LiveDashboardFixture fixture, string email)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/{fixture.Slug}/users?limit=100");
        request.Headers.Authorization = new("Bearer", fixture.ManagementKey);
        using var response = await fixture.AuthHttp.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var user in body.GetProperty("data").EnumerateArray())
        {
            if (user.GetProperty("email").GetString() == email)
                return user.GetProperty("id").GetString()!;
        }

        throw new InvalidOperationException($"ts-auth has no user for {email}");
    }

    [Test]
    public async Task StateRepositoryConflictsWhenAnotherWriterCommittedFirst()
    {
        var fixture = await Stack;
        var services = fixture.CreateDashboardFactory().Services;
        var repository = services.GetRequiredService<TerraScale.Dashboard.DashboardStateRepository>();
        var subject = $"conflict-{Guid.NewGuid():N}";

        var (missing, missingToken) = await repository.LoadAsync(subject, ct: default);
        await Assert.That(missing).IsNull();
        await Assert.That(missingToken).IsNull();

        // Writer A creates the world (no expectation yet) and reloads it so it
        // holds a real load-time token.
        var world = TerraScale.Dashboard.AccountWorld.Fresh("Conflict Probe", "conflict@example.test");
        await repository.SaveAsync(subject, world.RegisteredEmail, world, missingToken, ct: default);
        var (_, loadToken) = await repository.LoadAsync(subject, ct: default);
        await Assert.That(loadToken).IsNotNull();

        // Writer B commits first: the token advances past A's view.
        var competing = TerraScale.Dashboard.AccountWorld.Fresh("Writer B", "writer-b@example.test");
        await repository.SaveAsync(subject, competing.RegisteredEmail, competing, loadToken, ct: default);

        // Writer A still holds the load-time token: its save must surface the
        // conflict instead of silently overwriting B's committed world.
        await Assert.ThrowsAsync<TerraScale.Dashboard.DashboardStateConflictException>(
            async () => await repository.SaveAsync(subject, world.RegisteredEmail, world, loadToken, ct: default));

        // The committed world is B's, and a reload hands back the fresh token.
        var (reloaded, freshToken) = await repository.LoadAsync(subject, ct: default);
        await Assert.That(reloaded).IsNotNull();
        await Assert.That(reloaded!.RegisteredEmail).IsEqualTo("writer-b@example.test");
        await Assert.That(freshToken).IsNotEqualTo(loadToken);
    }

    /// <summary>RFC 7662 view of the session's refresh token, straight from ts-auth.</summary>
    private static async Task<JsonElement> IntrospectRefreshTokenAsync(LiveDashboardFixture fixture, string refreshToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/{fixture.Slug}/oauth/introspect")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refreshToken }),
        };
        request.Headers.Authorization = new(
            "Basic",
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{fixture.ClientId}:{fixture.ClientSecret}")));
        using var response = await fixture.AuthHttp.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
#endif
