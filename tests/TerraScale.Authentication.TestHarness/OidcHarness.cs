using System.Net;
using Microsoft.AspNetCore.Builder;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Tests;

/// <summary>Captures sent mail so tests can read sign-in codes. Nothing leaves the process.</summary>
public sealed partial class CapturingEmailSender : IEmailSender
{
    private readonly List<(string Recipient, EmailMessage Message)> sent = [];
    private TaskCompletionSource delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock counting = new();
    private int inFlight;
    private int maxInFlight;

    /// <summary>When set, every send waits for it before it is delivered.</summary>
    public TaskCompletionSource? Gate { get; init; }

    /// <summary>Sends in progress now.</summary>
    public int InFlight
    {
        get
        {
            lock (this.counting)
            {
                return this.inFlight;
            }
        }
    }

    /// <summary>The most sends ever in progress at once.</summary>
    public int MaxInFlight
    {
        get
        {
            lock (this.counting)
            {
                return this.maxInFlight;
            }
        }
    }

    public async Task SendAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
    {
        lock (this.counting)
        {
            this.maxInFlight = Math.Max(this.maxInFlight, ++this.inFlight);
        }

        try
        {
            if (this.Gate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            lock (this.counting)
            {
                this.sent.Add((recipient, message));
                this.delivered.TrySetResult();
                this.delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        finally
        {
            lock (this.counting)
            {
                this.inFlight--;
            }
        }
    }

    /// <summary>The next code mailed to <paramref name="recipient"/>; delivery happens after the response.</summary>
    public async Task<string> NextCodeAsync(string recipient)
    {
        var (_, message) = await this.NextAsync(recipient);
        return SixDigits().Match(message.Text).Value;
    }

    /// <summary>Waits for a recipient's mail without consuming another parallel journey's delivery.</summary>
    public async Task<(string Recipient, EmailMessage Message)> NextAsync(string? recipient = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            Task changed;
            lock (this.counting)
            {
                var index = this.sent.FindIndex(mail => recipient is null || string.Equals(mail.Recipient, recipient.Trim(), StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    var mail = this.sent[index];
                    this.sent.RemoveAt(index);
                    return mail;
                }

                changed = this.delivered.Task;
            }

            await changed.WaitAsync(timeout.Token);
        }
    }

    /// <summary>Whether any mail is waiting.</summary>
    public bool HasPending
    {
        get
        {
            lock (this.counting)
            {
                return this.sent.Count > 0;
            }
        }
    }

    [GeneratedRegex(@"\b[0-9]{6}\b")]
    private static partial Regex SixDigits();
}

/// <summary>
/// A real ts-auth server on a fresh real-TerraBase database, with the dashboard client registered from configuration.
/// Drives the browser and client sides of the OpenID Connect flows over HTTP.
/// </summary>
public sealed partial class OidcHarness : IAsyncDisposable
{
    public const string ClientSecret = "dashboard-test-secret-0123456789abcdef";
    public const string RedirectUri = "https://dash.example.test/auth/callback";
    public const string LoopbackRedirectUri = "http://localhost:5173/auth/callback";
    public const string PostLogoutRedirectUri = "https://dash.example.test/signed-out";
    public const string PublicOrigin = "https://auth.test.invalid";
    public const string Issuer = PublicOrigin + "/" + OidcIssuer.Slug;

    private readonly TerraBaseFixture? fixture;
    private readonly string masterKey;
    private readonly bool ownsServer;
    private readonly string prefix = AuthIds.New().ToLowerInvariant();

    private OidcHarness(TerraBaseFixture? fixture, TerraBaseTestTenant tenant, string masterKey, AuthTestServer server, TerraBaseSqlDatabase database, CapturingEmailSender email, bool ownsServer = true)
    {
        this.fixture = fixture;
        this.Tenant = tenant;
        this.masterKey = masterKey;
        this.Server = server;
        this.Database = database;
        this.Email = email;
        this.ownsServer = ownsServer;
    }

    public TerraBaseTestTenant Tenant { get; }

    public AuthTestServer Server { get; }

    public TerraBaseSqlDatabase Database { get; }

    public CapturingEmailSender Email { get; }

    public HttpClient Client => this.Server.Client;

    /// <summary>A management journey owns its users, organizations, projects and database names on the fixture host.</summary>
    public static async Task<OidcHarness> StartSharedManagementAsync(TerraBaseFixture fixture)
    {
        var shared = await fixture.ManagementAsync();
        return new(fixture, shared.Tenant, shared.masterKey, shared.Server, shared.Database, shared.Email, ownsServer: false);
    }

    public string UniqueName(string name) => name + "-" + this.prefix;

    public string UniqueEmail(string name) => this.UniqueName(name) + "@example.test";

    public static async Task<OidcHarness> StartAsync(TerraBaseFixture fixture, Action<Dictionary<string, string>>? settings = null, CapturingEmailSender? email = null,
        CapturingLogs? logs = null, TimeProvider? clock = null, Action<IServiceCollection>? services = null, bool runClosureWorker = true,
        Action<WebApplicationBuilder>? configure = null)
    {
        // The real application's startup bootstrap provisions its tables.
        // Repeating that setup here would create every table twice for each
        // harness without testing another behavior.
        var (tenant, database) = await fixture.CreateDatabaseAsync(provision: false);
        email ??= new CapturingEmailSender();
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var server = await StartServerAsync(fixture, tenant, email, values =>
        {
            values["master-key"] = masterKey;
            settings?.Invoke(values);
        }, logs, clock, services, runClosureWorker, configure);
        return new OidcHarness(fixture, tenant, masterKey, server, database, email);
    }

    /// <summary>Creates a database in this test's isolated TerraBase project and returns its immutable restriction.</summary>
    public async Task<string> CreateDatabaseIdAsync(string name)
    {
        var database = await this.fixture!.Server.Databases(this.Tenant.ApiKey).Database(this.Tenant.Project.Id, name).CreateAsync();
        return database.Id;
    }

    /// <summary>Another replica on this harness's database and master key; the caller disposes it.</summary>
    public Task<AuthTestServer> StartReplicaAsync(Action<Dictionary<string, string>>? settings = null, TimeProvider? clock = null, Action<IServiceCollection>? services = null, bool runClosureWorker = true) =>
        StartServerAsync(this.fixture ?? throw new InvalidOperationException("A storage fixture is required to start another auth server."), this.Tenant, this.Email, values =>
        {
            values["master-key"] = this.masterKey;
            settings?.Invoke(values);
        }, clock: clock, replace: services, runClosureWorker: runClosureWorker);

    /// <summary>
    /// Another replica bound to the same database, returned once its real startup
    /// bootstrap has provisioned the tables, platform and signing ring.
    /// </summary>
    public static async Task<AuthTestServer> StartServerAsync(TerraBaseFixture fixture, TerraBaseTestTenant tenant, CapturingEmailSender email, Action<Dictionary<string, string>>? settings = null,
        CapturingLogs? logs = null, TimeProvider? clock = null, Action<IServiceCollection>? replace = null, bool runClosureWorker = true,
        Action<WebApplicationBuilder>? configure = null)
    {
        var server = await AuthTestServer.StartAsync(values =>
        {
            values["dashboard-client-secret"] = ClientSecret;
            values["dashboard-redirect-uris"] = $"{RedirectUri};{LoopbackRedirectUri}";
            values["dashboard-post-logout-redirect-uris"] = PostLogoutRedirectUri;
            settings?.Invoke(values);
        }, services =>
        {
            fixture.UseStorage(services, tenant);
            // Ordinary OIDC flows use the production cache for immutable signing keys. Tests that
            // mutate keys across replicas explicitly register their refresh policy below.
            services.RemoveAll<KeyRingRefresh>();
            services.AddSingleton<IEmailSender>(email);
            if (logs is not null)
            {
                services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs);
            }

            if (clock is not null)
            {
                services.AddSingleton(clock);
            }

            replace?.Invoke(services);
        }, configure: configure, runClosureWorker: runClosureWorker);
        try
        {
            await server.App.Services.GetRequiredService<AuthSchema>().Ready.WaitAsync(TimeSpan.FromSeconds(30));
            return server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    public async Task<UserRecord> CreateUserAsync(string address = "alice@example.test") =>
        await this.Server.App.Services.GetRequiredService<Users>().CreateAsync(OidcIssuer.InstanceId, address)
            ?? throw new InvalidOperationException("The email already has an account.");

    /// <summary>Signs in through the hosted passcode pages and returns the session cookie value.</summary>
    public async Task<string> SignInAsync(string address, string? next = null)
    {
        using var request = await this.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-in", new() { ["email"] = address, ["next"] = next ?? string.Empty });
        if (request.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"Requesting a code answered {request.StatusCode}.");
        }

        var code = await this.Email.NextCodeAsync(address);
        using var verify = await this.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-in/verify", new() { ["email"] = address, ["code"] = code, ["next"] = next ?? string.Empty });
        return SessionCookie(verify) ?? throw new InvalidOperationException($"Verifying the code answered {verify.StatusCode} without a session cookie.");
    }

    /// <summary>A same-origin form post, as a browser on the hosted page sends it.</summary>
    public Task<HttpResponseMessage> PostFormAsync(string path, Dictionary<string, string> fields, string? origin = PublicOrigin, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) };
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return (client ?? this.Client).SendAsync(request);
    }

    public static string? SessionCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.Select(cookie => cookie.Split(';')[0]).Where(pair => pair.StartsWith("tsauth_session=", StringComparison.Ordinal))
                .Select(pair => pair["tsauth_session=".Length..]).LastOrDefault(value => value.Length > 0)
            : null;

    public static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    public static string AuthorizePath(string challenge, string state = "state-1", string nonce = "nonce-1", string redirectUri = RedirectUri,
        string scope = "openid email offline_access", string clientId = OidcIssuer.DashboardClientId, string method = "S256", string? extra = null) =>
        $"/{OidcIssuer.Slug}/oauth/authorize?response_type=code&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}"
        + $"&scope={Uri.EscapeDataString(scope)}&state={state}&nonce={nonce}&code_challenge={challenge}&code_challenge_method={method}{extra}";

    public async Task<HttpResponseMessage> AuthorizeAsync(string path, string? session)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (session is not null)
        {
            request.Headers.Add("Cookie", $"tsauth_session={session}");
        }

        return await this.Client.SendAsync(request);
    }

    /// <summary>Runs authorize with a live session and returns the code from the callback redirect.</summary>
    public async Task<string> CodeAsync(string session, string challenge, string redirectUri = RedirectUri)
    {
        using var response = await this.AuthorizeAsync(AuthorizePath(challenge, redirectUri: redirectUri), session);
        var location = response.Headers.Location ?? throw new InvalidOperationException($"Authorize answered {response.StatusCode} without a redirect.");
        if (!location.AbsoluteUri.StartsWith(redirectUri + "?", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Authorize redirected to {location}.");
        }

        return Query(location)["code"] ?? throw new InvalidOperationException($"No code in {location}.");
    }

    public Task<HttpResponseMessage> TokenAsync(Dictionary<string, string> form, string secret = ClientSecret, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/{OidcIssuer.Slug}/oauth/token") { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{OidcIssuer.DashboardClientId}:{secret}")));
        return (client ?? this.Client).SendAsync(request);
    }

    public Task<HttpResponseMessage> RedeemCodeAsync(string code, string verifier, string redirectUri = RedirectUri, HttpClient? client = null) =>
        this.TokenAsync(new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirectUri, ["code_verifier"] = verifier }, client: client);

    public Task<HttpResponseMessage> RefreshAsync(string refreshToken, HttpClient? client = null) =>
        this.TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }, client: client);

    /// <summary>Signs a fresh user in and redeems a code: the full browser and client flow.</summary>
    public async Task<(UserRecord User, string Session, JsonElement Tokens)> SignInAndRedeemAsync(string address = "alice@example.test")
    {
        var user = await this.CreateUserAsync(address);
        var session = await this.SignInAsync(address);
        var (verifier, challenge) = Pkce();
        using var response = await this.RedeemCodeAsync(await this.CodeAsync(session, challenge), verifier);
        var body = await response.Content.ReadAsStringAsync();
        return response.StatusCode == HttpStatusCode.OK
            ? (user, session, JsonSerializer.Deserialize<JsonElement>(body))
            : throw new InvalidOperationException($"The token endpoint answered {response.StatusCode}: {body}");
    }

    /// <summary>Seeds a stored session for setup, then issues tokens through real HTTP authorization and PKCE redemption.</summary>
    public async Task<(string Session, JsonElement Tokens)> CreateSessionAndRedeemAsync(UserRecord user)
    {
        var session = await this.Server.App.Services.GetRequiredService<TerraScale.Authentication.Features.Identity.Sessions>().CreateAsync(user);
        var (verifier, challenge) = Pkce();
        using var response = await this.RedeemCodeAsync(await this.CodeAsync(session.Token, challenge), verifier);
        response.EnsureSuccessStatusCode();
        return (session.Token, await JsonAsync(response));
    }

    /// <summary>Revokes a token at the RFC 7009 endpoint.</summary>
    public Task<HttpResponseMessage> RevokeAsync(string token, string hint = "refresh_token")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/{OidcIssuer.Slug}/oauth/revoke") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token, ["token_type_hint"] = hint }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{OidcIssuer.DashboardClientId}:{ClientSecret}")));
        return this.Client.SendAsync(request);
    }

    /// <summary>The <c>error.code</c> of a JSON error envelope.</summary>
    public static async Task<string?> EnvelopeCodeAsync(HttpResponseMessage response) =>
        (await JsonAsync(response)).GetProperty("error").GetProperty("code").GetString();

    /// <summary>Calls userinfo with a bearer access token.</summary>
    public async Task<HttpResponseMessage> UserInfoAsync(string accessToken, HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/{OidcIssuer.Slug}/oauth/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await (client ?? this.Client).SendAsync(request);
    }

    /// <summary>The OAuth <c>error</c> code of a JSON error response.</summary>
    public static async Task<string?> ErrorAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync()).TryGetProperty("error", out var error) ? error.GetString() : null;

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

    public static Dictionary<string, string?> Query(Uri uri) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query).ToDictionary(pair => pair.Key, pair => (string?)pair.Value.ToString(), StringComparer.Ordinal);

    /// <summary>Maps a URL the server advertises under its public origin onto the loopback listener.</summary>
    public static string Local(string advertised) =>
        advertised.StartsWith(PublicOrigin + "/", StringComparison.Ordinal) ? advertised[PublicOrigin.Length..] : throw new InvalidOperationException($"{advertised} is not under the public origin.");

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public ValueTask DisposeAsync() => this.ownsServer ? this.Server.DisposeAsync() : ValueTask.CompletedTask;
}
