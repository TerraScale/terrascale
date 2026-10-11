#:sdk Microsoft.NET.Sdk.Web
#:property PublishAot=false
#:project ../../TerraScale.Dashboard.csproj
#:package Microsoft.AspNetCore.TestHost@10.0.12

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

var root = args[0];
var state = new TestState();
var auth = new TestAuth();
var identity = new TestIdentity();
var clock = new TestClock();
await using var first = CreateHost();
await using var second = CreateHost();
await first.StartAsync();
await second.StartAsync();
var client = first.GetTestClient();
var other = second.GetTestClient();
client.BaseAddress = other.BaseAddress = new Uri("https://dashboard.test");

var page = await client.GetAsync("/login?redirectTo=https://evil.test/");
Check(page.StatusCode == HttpStatusCode.OK, "login renders");
var html = await page.Content.ReadAsStringAsync();
Check(html.Contains("value=\"/\""), "external redirect is rejected");
Check(html.Contains("/brand/logo-terrascale-blue.svg?v="), "file-backed brand is fingerprinted");
Check(html.Contains("data-on:input") && html.Contains("data-attr:type"), "Datastar password reveal is present");
Check(page.Headers.CacheControl?.NoStore == true, "auth HTML is never cached");
Check(!page.Headers.GetValues("Content-Security-Policy").Single().Contains("unsafe-eval"), "CSP requires nonced script evaluation");
var csrf = Match(html, "name=\"_csrf\" value=\"([^\"]+)\"");
var csrfCookie = page.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
Check(page.Headers.GetValues("Set-Cookie").Single().Contains("secure", StringComparison.OrdinalIgnoreCase), "HTTPS CSRF cookie is secure");
var noCsrf = await Post(client, "/login", "", new() { ["email"] = "user@example.com", ["password"] = "test-password" });
Check(noCsrf.StatusCode == HttpStatusCode.Forbidden && auth.SignIns == 0, "missing CSRF never reaches ts-auth");
using var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "/login")
{
    Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["_csrf"] = csrf, ["email"] = "user@example.com", ["password"] = "test-password" })
};
crossOrigin.Headers.Add("Cookie", csrfCookie);
crossOrigin.Headers.Add("Origin", "https://attacker.test");
Check((await client.SendAsync(crossOrigin)).StatusCode == HttpStatusCode.Forbidden && auth.SignIns == 0, "cross-origin submissions are rejected even with matching tokens");
var badEmail = await Post(client, "/login", csrfCookie, new() { ["_csrf"] = csrf, ["email"] = "invalid", ["password"] = "test-password" });
Check((int)badEmail.StatusCode == 422 && auth.SignIns == 0, "invalid email never reaches ts-auth");
var badPassword = await Post(client, "/login", csrfCookie, new() { ["_csrf"] = csrf, ["email"] = "user@example.com", ["password"] = "wrong" });
Check((int)badPassword.StatusCode == 422, "rejected sign-in renders an error");
var signedIn = await Post(client, "/login", csrfCookie, new()
{
    ["_csrf"] = csrf, ["email"] = "user@example.com", ["password"] = "test-password", ["redirectTo"] = "//evil.test"
});
Check(signedIn.StatusCode == HttpStatusCode.Found && signedIn.Headers.Location?.ToString() == "/", "successful login keeps redirects local");
var sessionCookie = signedIn.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("ts-dashboard-session=")).Split(';')[0];
Check(state.Items.Count == 1, "session is written to shared state");
var stored = state.Items.Single();
var bearer = sessionCookie.Split('=')[1];
Check(stored.Key.EndsWith(Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(bearer)))), "state key contains only a bearer hash");
Check(!stored.Value.Document.GetRawText().Contains("test-access") && !stored.Value.Document.GetRawText().Contains("user@example.com"), "stored session encrypts tokens and identity");
other.DefaultRequestHeaders.Add("Cookie", sessionCookie);
var replicated = await other.GetAsync("/__test/session");
Check(replicated.StatusCode == HttpStatusCode.OK && (await replicated.Content.ReadAsStringAsync()).Contains("test-user"), "another host reads the persisted session");

auth.Active = false;
Check((await other.GetAsync("/__test/session")).StatusCode == HttpStatusCode.Unauthorized, "ts-auth revocation invalidates a persisted session");
Check(state.Items.IsEmpty, "revoked session is removed");
auth.Active = true;
signedIn = await Post(client, "/login", csrfCookie, new() { ["_csrf"] = csrf, ["email"] = "user@example.com", ["password"] = "test-password" });
sessionCookie = signedIn.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("ts-dashboard-session=")).Split(';')[0];
clock.Advance(TimeSpan.FromHours(2));
other.DefaultRequestHeaders.Remove("Cookie");
other.DefaultRequestHeaders.Add("Cookie", sessionCookie);
Check((await other.GetAsync("/__test/session")).StatusCode == HttpStatusCode.Unauthorized && state.Items.IsEmpty, "expired session is removed across hosts");

var code = await Post(client, "/login/code", csrfCookie, new() { ["_csrf"] = csrf, ["email"] = "user@example.com" });
Check((await code.Content.ReadAsStringAsync()).Contains("autocomplete=\"one-time-code\""), "email-code flow renders verification");
var reset = await Post(client, "/reset-password", csrfCookie, new() { ["_csrf"] = csrf, ["email"] = "user@example.com" });
Check((await reset.Content.ReadAsStringAsync()).Contains("Confirm new password"), "reset flow renders confirmation");
var badReset = await Post(client, "/reset-password/complete", csrfCookie, new() { ["_csrf"] = csrf, ["email"] = "user@example.com", ["code"] = "123456", ["password"] = "long-password", ["confirmPassword"] = "different" });
Check((int)badReset.StatusCode == 422 && auth.Resets == 0, "password mismatch never reaches ts-auth");
var goodReset = await Post(client, "/reset-password/complete", csrfCookie, new() { ["_csrf"] = csrf, ["email"] = "user@example.com", ["code"] = "123456", ["password"] = "long-password", ["confirmPassword"] = "long-password" });
Check(goodReset.StatusCode == HttpStatusCode.OK && auth.Resets == 1, "password reset reaches ts-auth");
var missing = await client.GetAsync("/missing");
Check(missing.StatusCode == HttpStatusCode.NotFound && (await missing.Content.ReadAsStringAsync()).Contains("404-illustration.webp"), "illustrated 404 preserves the assets");
Check((await client.GetAsync("/api/missing")).StatusCode == HttpStatusCode.NotFound, "API fallback remains 404");
var asset = await client.GetAsync(Match(html, "rel=\"stylesheet\" href=\"([^\"]+)\""));
Check(asset.StatusCode == HttpStatusCode.OK && asset.Headers.CacheControl?.Extensions.Any(value => value.Name == "immutable") == true, "matching asset fingerprint caches immutably");
var stale = await client.GetAsync("/css/terrascale.css?v=stale");
Check(stale.Headers.CacheControl?.Extensions.Any(value => value.Name == "immutable") != true, "stale fingerprint is not cached immutably");
var signals = await client.GetAsync("/__test/signals");
Check(signals.Content.Headers.ContentType?.MediaType == "text/event-stream" &&
      (await signals.Content.ReadAsStringAsync()).Contains("event: datastar-patch-signals\ndata: signals {\"count\":1}\n\n"), "Datastar SSE follows the wire contract");
Check((await signals.Content.ReadAsStringAsync()).Contains("event: datastar-patch-signals\ndata: signals {\"count\":2}\n\n"), "Datastar streams more than one event without changing sent headers");
state.ReadyError = new HttpRequestException("synthetic dependency failure");
var unavailable = await client.GetAsync("/readyz");
Check(unavailable.StatusCode == HttpStatusCode.ServiceUnavailable && state.Items.IsEmpty, "dependency failure is 503 without a memory fallback or seeded state");
state.ReadyError = new TimeoutException("synthetic request deadline");
Check((await client.GetAsync("/readyz")).StatusCode == HttpStatusCode.ServiceUnavailable, "dependency timeout is 503");
state.ReadyError = null;
var elements = await client.GetAsync("/__test/elements");
var elementStream = await elements.Content.ReadAsStringAsync();
Check(elements.StatusCode == HttpStatusCode.OK && elementStream.Contains("event: datastar-patch-elements") &&
      elementStream.Contains("/brand/region-map.svg?v="), "Razor HTML patches resolve request-scoped injected services");
Console.WriteLine("PASS: authentication, CSRF, redirect safety, encrypted shared sessions, revocation, expiry, reset, assets, 404, Datastar SSE");

WebApplication CreateHost()
{
    var app = DashboardApplication.Build(["--contentRoot", root], builder =>
    {
        builder.WebHost.UseTestServer();
        builder.Services.Replace(ServiceDescriptor.Singleton<IDashboardState>(state));
        builder.Services.Replace(ServiceDescriptor.Singleton<IAuthGateway>(auth));
        builder.Services.Replace(ServiceDescriptor.Singleton<IDashboardIdentity>(identity));
        builder.Services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
        builder.Logging.ClearProviders();
    });
    app.MapGet("/__test/session", async (HttpContext context, DashboardSessions sessions) =>
        await sessions.ReadAsync(context) is { } session ? Results.Json(new { session.UserId }) : (IResult)Results.Unauthorized());
    app.MapGet("/__test/signals", async (HttpContext context) =>
    {
        await DatastarEvents.PatchSignalsAsync(context, new { count = 1 });
        await DatastarEvents.PatchSignalsAsync(context, new { count = 2 });
    });
    app.MapGet("/__test/elements", (HttpContext context) => DatastarEvents.PatchElementsAsync(context, TerraScale.Dashboard.UI.Slices.RegionMap.Create()));
    return app;
}

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
}
static string Match(string text, string pattern) => Regex.Match(text, pattern).Groups[1].Value;
static Task<HttpResponseMessage> Post(HttpClient client, string path, string cookie, Dictionary<string, string> body)
{
    var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(body) };
    if (cookie.Length != 0) request.Headers.Add("Cookie", cookie);
    return client.SendAsync(request);
}

sealed class TestIdentity : IDashboardIdentity
{
    private readonly DashboardCredentials credentials = new("test-project", "test-api-key", "test-management", "test-instance", "test-client", "test-client-secret", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    public Task<DashboardCredentials> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(credentials);
}
sealed class TestClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan delta) => now += delta;
}
sealed class TestAuth : IAuthGateway
{
    public bool Active { get; set; } = true;
    public int SignIns { get; private set; }
    public int Resets { get; private set; }
    public Task<SessionRecord> PasswordAsync(string email, string password, string ip, CancellationToken cancellationToken)
    {
        SignIns++;
        if (password != "test-password") throw new SignInRejectedException();
        return Task.FromResult(new SessionRecord("test-user", "Test User", email, "test-access", "test-refresh", DateTimeOffset.UtcNow.AddHours(1)));
    }
    public Task<SessionRecord> EmailCodeAsync(string email, string code, string ip, CancellationToken cancellationToken) => PasswordAsync(email, "test-password", ip, cancellationToken);
    public Task StartEmailCodeAsync(string email, string ip, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartPasswordResetAsync(string email, string ip, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CompletePasswordResetAsync(string email, string code, string password, string ip, CancellationToken cancellationToken) { Resets++; return Task.CompletedTask; }
    public Task<bool> IsActiveAsync(SessionRecord session, CancellationToken cancellationToken) => Task.FromResult(Active);
    public Task RevokeAsync(SessionRecord session, CancellationToken cancellationToken) { Active = false; return Task.CompletedTask; }
}
sealed class TestState : IDashboardState
{
    public ConcurrentDictionary<string, TerraBaseDocument> Items { get; } = new();
    public Exception? ReadyError { get; set; }
    public Task CheckReadyAsync(CancellationToken cancellationToken) => ReadyError is null ? Task.CompletedTask : Task.FromException(ReadyError);
    public Task<TerraBaseDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken) => Task.FromResult(Items.GetValueOrDefault(collection + "/" + id));
    public Task PutAsync(string collection, string id, JsonElement document, TerraBasePrecondition precondition, CancellationToken cancellationToken)
    {
        if (!Items.TryAdd(collection + "/" + id, new TerraBaseDocument(id, "\"tbv1-test\"", "live", document))) throw new Exception("Test create-only precondition failed");
        return Task.CompletedTask;
    }
    public Task DeleteAsync(string collection, string id, string version, CancellationToken cancellationToken)
    {
        var key = collection + "/" + id;
        if (Items.GetValueOrDefault(key)?.Version != version) throw new Exception("Test version precondition failed");
        Items.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
