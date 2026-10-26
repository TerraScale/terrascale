using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Primitives;

namespace TerraBase.Testing;

/// <summary>
/// An in-process stand-in for ts-auth: its OIDC discovery document and JWKS, and access tokens signed with its keys.
/// Starts with one published ES256 key that signs every token. <see cref="Route"/> serves the documents through a message
/// handler that replaces the server's transport (no socket); <see cref="ListenAsync"/> serves them over TLS on a loopback
/// port to the server's own transport.
/// </summary>
public sealed class TestIssuer : IDisposable
{
    public const string Issuer = "https://auth.test/terrascale";
    public const string Audience = "https://terrabase.test";
    public const string Discovery = Issuer + "/.well-known/openid-configuration";
    public const string Jwks = Issuer + "/.well-known/jwks.json";

    /// <summary>Where <see cref="RedirectJwks"/> sends the JWKS request; it serves the same key set.</summary>
    public const string MovedJwks = Issuer + "/.well-known/moved-jwks.json";

    private static readonly JsonWebTokenHandler Tokens = new() { SetDefaultTimesOnTokenCreation = false };
    private static readonly Uri IssuerAddress = new(Issuer);
    private static readonly string ProjectAuthorityPath = "/v1" + IssuerAddress.AbsolutePath.TrimEnd('/') + "/organizations/";

    private readonly Lock _gate = new();
    private readonly List<JsonObject> _published = [];
    private readonly List<IDisposable> _keys = [];
    private readonly Dictionary<(string Token, string Organization, string Project), ProjectAuthority> _projects = [];
    private readonly Dictionary<string, RecoveryAuthority> _recovery = [];
    private readonly Dictionary<string, Func<string>> _clients = [];
    private readonly Dictionary<string, string?> _clientStatus = [];
    private int _statusRequests;
    private int _tokenRequests;
    private int _discoveryFetches;
    private int _jwksFetches;
    private int _movedJwksFetches;
    private int _projectFetches;
    private volatile bool _available = true;
    private readonly TaskCompletionSource _hanging = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TestIssuer()
    {
        Signing = AddKey(SecurityAlgorithms.EcdsaSha256);
    }

    /// <summary>The credentials <see cref="Mint"/> signs with.</summary>
    public SigningCredentials Signing { get; private set; }

    /// <summary>Whether ts-auth answers; while false every fetch fails as a refused connection does.</summary>
    public bool Available
    {
        get => _available;
        set => _available = value;
    }

    /// <summary>Whether requests for the JWKS hang until the caller cancels them.</summary>
    public bool HangJwks { get; set; }

    /// <summary>Completes when a JWKS request starts hanging (<see cref="HangJwks"/>).</summary>
    public Task JwksHanging => _hanging.Task;

    /// <summary>Whether the JWKS answers <c>302</c> to <see cref="MovedJwks"/>, which serves a valid key set.</summary>
    public bool RedirectJwks { get; set; }

    /// <summary>Bytes of filler added to the JWKS document, to make it larger than the server reads.</summary>
    public int JwksPaddingBytes { get; set; }

    /// <summary>The <c>issuer</c> the discovery document names; tests change it to serve a mismatched document.</summary>
    public string DiscoveryIssuer { get; set; } = Issuer;

    /// <summary>Requests for the discovery document, including those that failed because ts-auth was unavailable.</summary>
    public int DiscoveryFetches => Volatile.Read(ref _discoveryFetches);

    /// <summary>Requests for the JWKS.</summary>
    public int JwksFetches => Volatile.Read(ref _jwksFetches);

    /// <summary>Requests for <see cref="MovedJwks"/>.</summary>
    public int MovedJwksFetches => Volatile.Read(ref _movedJwksFetches);

    /// <summary>Requests for a project's ts-auth authority, including denied requests.</summary>
    public int ProjectFetches => Volatile.Read(ref _projectFetches);

    /// <summary>An explicit response to a project authority request; defaults to admin access to the requested project.</summary>
    public sealed record ProjectAuthority(
        string Id, string OrganizationId, string Role = "admin", HttpStatusCode Status = HttpStatusCode.OK,
        Uri? Redirect = null, bool Available = true);

    /// <summary>Grants only this exact bearer token access to this organization's project; no token is granted by default.</summary>
    public void GrantProject(string token, string organization, string project, ProjectAuthority? authority = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(organization);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        lock (_gate)
        {
            _projects[(token, organization, project)] = authority ?? new ProjectAuthority(project, organization);
        }
    }

    /// <summary>Explicit live subject and organization responses for recovery; no token is granted by default.</summary>
    public sealed record RecoveryAuthority(string Subject, string Organization, string? Role = "owner",
        HttpStatusCode Status = HttpStatusCode.OK, Uri? Redirect = null, bool Available = true, bool Malformed = false,
        Func<Task>? BeforeOrganization = null);

    /// <summary>Grants only this token the supplied live recovery identity and organization response.</summary>
    public void GrantRecovery(string token, RecoveryAuthority authority)
    {
        lock (_gate)
        {
            _recovery[token] = authority;
        }
    }

    /// <summary>Client credentials grants at <c>{Issuer}/oauth/token</c>, granted or not.</summary>
    public int TokenRequests => Volatile.Read(ref _tokenRequests);

    public int StatusRequests => Volatile.Read(ref _statusRequests);

    public bool StatusAvailable { get; set; } = true;

    public static string KeyGeneration(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];

    public void ExpireClient(string key)
    {
        lock (_gate)
        {
            _clientStatus[key[..26]] = "key_expired";
        }
    }

    /// <summary>
    /// Issues <paramref name="mint"/>'s access token for the dashboard API key <paramref name="key"/> (<c>&lt;key id&gt;_&lt;secret&gt;</c>)
    /// through the client credentials grant, as ts-auth does; any other credential gets <c>401</c>.
    /// </summary>
    public void GrantClient(string key, Func<string> mint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            _clientStatus[key[..26]] = null;
            _clients["Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(key[..key.IndexOf('_', StringComparison.Ordinal)] + ":" + key))] = mint;
        }
    }

    /// <summary>Revokes a key <see cref="GrantClient"/> granted.</summary>
    public void RevokeClient(string key)
    {
        lock (_gate)
        {
            _clientStatus[key[..26]] = "key_revoked";
            _clients.Remove("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(key[..key.IndexOf('_', StringComparison.Ordinal)] + ":" + key)));
        }
    }

    /// <summary>Points <paramref name="settings"/> at <see cref="Issuer"/> with <see cref="Audience"/>.</summary>
    public static void Configure(Dictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings["auth-issuer"] = Issuer;
        settings["auth-audience"] = Audience;
    }

    /// <summary>Routes the server's ts-auth client to this issuer through a handler that replaces its transport.</summary>
    public void Route(IServiceCollection services) =>
        services.AddHttpClient("ts-auth").ConfigurePrimaryHttpMessageHandler(() => new Handler(this));

    /// <summary>Serves this issuer over TLS on a loopback port; <see cref="TlsIssuer.Route"/> points the server's own transport at it.</summary>
    public Task<TlsIssuer> ListenAsync() => TlsIssuer.StartAsync(this);

    /// <summary>
    /// Creates a key for <paramref name="algorithm"/> (ES256, RS256 or HS256), publishes it in the JWKS unless
    /// <paramref name="publish"/> is false, and returns credentials that sign with it. <see cref="Signing"/> is unchanged.
    /// </summary>
    public SigningCredentials AddKey(string algorithm, bool publish = true)
    {
        var kid = EntityIds.New().ToString();
        SecurityKey key;
        JsonObject jwk;
        if (algorithm == SecurityAlgorithms.HmacSha256)
        {
            var secret = RandomNumberGenerator.GetBytes(32);
            key = new SymmetricSecurityKey(secret) { KeyId = kid };
            jwk = new JsonObject { ["kty"] = "oct", ["k"] = Base64UrlEncoder.Encode(secret) };
        }
        else if (algorithm == SecurityAlgorithms.RsaSha256)
        {
            var rsa = RSA.Create(2048);
            Track(rsa);
            var parameters = rsa.ExportParameters(includePrivateParameters: false);
            key = new RsaSecurityKey(rsa) { KeyId = kid };
            jwk = new JsonObject { ["kty"] = "RSA", ["n"] = Base64UrlEncoder.Encode(parameters.Modulus), ["e"] = Base64UrlEncoder.Encode(parameters.Exponent) };
        }
        else
        {
            var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Track(ecdsa);
            var point = ecdsa.ExportParameters(includePrivateParameters: false).Q;
            key = new ECDsaSecurityKey(ecdsa) { KeyId = kid };
            jwk = new JsonObject { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = Base64UrlEncoder.Encode(point.X), ["y"] = Base64UrlEncoder.Encode(point.Y) };
        }

        if (publish)
        {
            jwk["kid"] = kid;
            jwk["use"] = "sig";
            jwk["alg"] = algorithm;
            lock (_gate)
            {
                _published.Add(jwk);
            }
        }

        return new SigningCredentials(key, algorithm);
    }

    /// <summary>Removes <paramref name="credentials"/>' key from the JWKS, as ts-auth does when it retires a key.</summary>
    public void Withdraw(SigningCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        lock (_gate)
        {
            _published.RemoveAll(key => (string?)key["kid"] == credentials.Key.KeyId);
        }
    }

    /// <summary>Publishes a new ES256 key and signs every later token with it; the old key stays published.</summary>
    public SigningCredentials Rotate() => Signing = AddKey(SecurityAlgorithms.EcdsaSha256);

    /// <summary>
    /// An access token for <paramref name="subject"/>: type <c>at+jwt</c>, this issuer, <see cref="Audience"/>, valid
    /// from a minute ago for five minutes, signed with <see cref="Signing"/>. <paramref name="edit"/> changes it first;
    /// claims go in <see cref="SecurityTokenDescriptor.Claims"/>.
    /// </summary>
    public string Mint(string? subject, Action<SecurityTokenDescriptor>? edit = null)
    {
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            TokenType = "at+jwt",
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now,
            NotBefore = now.AddMinutes(-1),
            Expires = now.AddMinutes(5),
            Claims = new Dictionary<string, object>(StringComparer.Ordinal),
            SigningCredentials = Signing,
        };
        if (subject is not null)
        {
            descriptor.Claims[JwtRegisteredClaimNames.Sub] = subject;
        }

        edit?.Invoke(descriptor);
        // ts-auth signs every project token with the organization that owns the project.
        if (descriptor.Claims.ContainsKey("project_id"))
        {
            descriptor.Claims.TryAdd("org_id", EntityIds.New().ToString());
        }

        return Tokens.CreateToken(descriptor);
    }

    /// <summary>An access token for <paramref name="subject"/> whose verified email is <paramref name="email"/>.</summary>
    public string MintWithEmail(string subject, string email, bool verified = true) => Mint(subject, token =>
    {
        token.Claims[JwtRegisteredClaimNames.Email] = email;
        token.Claims["email_verified"] = verified;
    });

    /// <summary>An unsigned (<c>alg: none</c>) token that is otherwise valid for <paramref name="subject"/>.</summary>
    public static string MintUnsigned(string subject)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = new JsonObject { ["alg"] = "none", ["typ"] = "at+jwt" };
        var payload = new JsonObject { ["iss"] = Issuer, ["aud"] = Audience, ["sub"] = subject, ["iat"] = now, ["exp"] = now + 300 };
        return $"{Base64UrlEncoder.Encode(header.ToJsonString())}.{Base64UrlEncoder.Encode(payload.ToJsonString())}.";
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var key in _keys)
            {
                key.Dispose();
            }

            _keys.Clear();
            _projects.Clear();
            _clients.Clear();
        }
    }

    private void Track(IDisposable key)
    {
        lock (_gate)
        {
            _keys.Add(key);
        }
    }

    // Answers one request for uri, as ts-auth would.
    private async Task<HttpResponseMessage> RespondAsync(Uri uri, string method, string? authorization, CancellationToken cancellationToken)
    {
        if (HangJwks && uri.AbsoluteUri == Jwks)
        {
            _hanging.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        RecoveryAuthority? recovery;
        lock (_gate)
        {
            recovery = authorization?.StartsWith("Bearer ", StringComparison.Ordinal) == true
                ? _recovery.GetValueOrDefault(authorization[7..]) : null;
        }

        if (recovery?.BeforeOrganization is { } before && uri.AbsolutePath == ProjectAuthorityPath + recovery.Organization)
        {
            await before();
        }

        return Serve(uri, method, authorization);
    }

    private HttpResponseMessage Serve(Uri uri, string method, string? authorization)
    {
        if (uri.AbsoluteUri == Discovery)
        {
            Interlocked.Increment(ref _discoveryFetches);
        }

        if (!Available)
        {
            throw new HttpRequestException("connection refused (test issuer unavailable)");
        }

        if (uri.GetLeftPart(UriPartial.Authority) == IssuerAddress.GetLeftPart(UriPartial.Authority)
            && uri.AbsolutePath.StartsWith(ProjectAuthorityPath, StringComparison.Ordinal)
            && uri.AbsolutePath[ProjectAuthorityPath.Length..].Split('/') is [var organization, "projects", var project])
        {
            Interlocked.Increment(ref _projectFetches);
            if (method != "GET")
            {
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }

            ProjectAuthority? authority;
            lock (_gate)
            {
                authority = authorization?.StartsWith("Bearer ", StringComparison.Ordinal) == true
                    ? _projects.GetValueOrDefault((authorization[7..], organization, project)) : null;
            }

            if (authority is null)
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            if (!authority.Available)
            {
                throw new HttpRequestException("connection refused (test project authority unavailable)");
            }

            var result = new HttpResponseMessage(authority.Status)
            {
                Content = new StringContent(new JsonObject
                {
                    ["id"] = authority.Id,
                    ["organization_id"] = authority.OrganizationId,
                    ["role"] = authority.Role,
                }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            result.Headers.Location = authority.Redirect;
            return result;
        }

        if (uri.GetLeftPart(UriPartial.Authority) == IssuerAddress.GetLeftPart(UriPartial.Authority)
            && (uri.AbsolutePath == IssuerAddress.AbsolutePath + "/oauth/userinfo"
                || uri.AbsolutePath.StartsWith(ProjectAuthorityPath, StringComparison.Ordinal)))
        {
            RecoveryAuthority? recovery;
            lock (_gate)
            {
                recovery = authorization?.StartsWith("Bearer ", StringComparison.Ordinal) == true
                    ? _recovery.GetValueOrDefault(authorization[7..]) : null;
            }

            if (method != "GET" || recovery is null)
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            if (!recovery.Available)
            {
                throw new HttpRequestException("test authority unavailable");
            }

            var subject = uri.AbsolutePath == IssuerAddress.AbsolutePath + "/oauth/userinfo";
            var payload = subject ? new JsonObject { ["sub"] = recovery.Subject }
                : new JsonObject { ["id"] = recovery.Organization, ["role"] = recovery.Role };
            var response = new HttpResponseMessage(recovery.Status)
            {
                Content = new StringContent(recovery.Malformed ? "{" : payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            response.Headers.Location = recovery.Redirect;
            return response;
        }

        if (uri.AbsoluteUri == "https://auth.test/v1/terrascale/api-keys/self" && method == "GET")
        {
            Interlocked.Increment(ref _statusRequests);
            if (!StatusAvailable)
            {
                throw new HttpRequestException("test key status unavailable");
            }

            var jwt = authorization?.StartsWith("Bearer ", StringComparison.Ordinal) == true ? new JsonWebToken(authorization[7..]) : null;
            var id = jwt?.TryGetPayloadValue<string>("key_id", out var found) == true ? found : string.Empty;
            string? refusal;
            lock (_gate)
            {
                refusal = _clientStatus.TryGetValue(id, out var status) ? status : "key_revoked";
            }

            return new HttpResponseMessage(refusal is null ? HttpStatusCode.OK : HttpStatusCode.Unauthorized)
            {
                Content = new StringContent((refusal is null ? new JsonObject { ["key_id"] = id }
                    : new JsonObject { ["error"] = new JsonObject { ["code"] = refusal, ["message"] = "x" } }).ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }

        if (uri.AbsoluteUri == Issuer + "/oauth/token" && method == "POST")
        {
            Interlocked.Increment(ref _tokenRequests);
            Func<string>? mint;
            lock (_gate)
            {
                mint = authorization is null ? null : _clients.GetValueOrDefault(authorization);
            }

            return mint is null
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(new JsonObject { ["access_token"] = mint(), ["token_type"] = "Bearer", ["expires_in"] = 900 }.ToJsonString(),
                        Encoding.UTF8, "application/json"),
                };
        }

        string body;
        if (uri.AbsoluteUri == Discovery)
        {
            body = new JsonObject { ["issuer"] = DiscoveryIssuer, ["jwks_uri"] = Jwks }.ToJsonString();
        }
        else if (uri.AbsoluteUri == Jwks && RedirectJwks)
        {
            Interlocked.Increment(ref _jwksFetches);
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri(MovedJwks);
            return redirect;
        }
        else if (uri.AbsoluteUri is Jwks or MovedJwks)
        {
            Interlocked.Increment(ref uri.AbsoluteUri == Jwks ? ref _jwksFetches : ref _movedJwksFetches);
            lock (_gate)
            {
                var keySet = new JsonObject { ["keys"] = new JsonArray([.. _published.Select(key => key.DeepClone())]) };
                if (JwksPaddingBytes > 0)
                {
                    keySet["padding"] = new string('x', JwksPaddingBytes);
                }

                body = keySet.ToJsonString();
            }
        }
        else
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    // One per handler the client factory builds; the factory disposes handlers, never the issuer.
    private sealed class Handler(TestIssuer issuer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            issuer.RespondAsync(request.RequestUri!, request.Method.Method, request.Headers.Authorization?.ToString(), cancellationToken);
    }

    /// <summary>
    /// A <see cref="TestIssuer"/> served by Kestrel over TLS on a loopback port with a self-signed certificate. Unlike
    /// <see cref="TestIssuer.Route"/>, it keeps the server's own <c>ts-auth</c> transport and its settings (no redirects,
    /// the response size limit); only where it connects and which certificate it trusts change.
    /// </summary>
    public sealed class TlsIssuer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly X509Certificate2 _certificate;
        private readonly int _port;

        private TlsIssuer(WebApplication app, X509Certificate2 certificate, int port)
        {
            _app = app;
            _certificate = certificate;
            _port = port;
        }

        /// <summary>Points the server's ts-auth transport at this listener, trusting only its certificate.</summary>
        public void Route(IServiceCollection services) =>
            services.AddHttpClient("ts-auth").ConfigurePrimaryHttpMessageHandler((handler, _) =>
            {
                // The production handler, configured, never replaced: a different handler type fails the test here.
                var sockets = (SocketsHttpHandler)handler;
                sockets.ConnectCallback = ConnectAsync;
                sockets.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, _) =>
                    presented is not null && presented.GetCertHash().AsSpan().SequenceEqual(_certificate.GetCertHash());
            });

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _app.DisposeAsync();
            }
            finally
            {
                _certificate.Dispose();
            }
        }

        internal static async Task<TlsIssuer> StartAsync(TestIssuer issuer)
        {
            X509Certificate2 certificate;
            using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                certificate = new CertificateRequest("CN=auth.test", key, HashAlgorithmName.SHA256)
                    .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            }

            WebApplication? app = null;
            try
            {
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
                builder.Configuration.Sources.Clear();
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
                app = builder.Build();
                app.Run(async context =>
                {
                    using var response = await issuer.RespondAsync(new Uri(context.Request.GetEncodedUrl()), context.Request.Method,
                        context.Request.Headers.Authorization.ToString(), context.RequestAborted);
                    context.Response.StatusCode = (int)response.StatusCode;
                    if (response.Headers.Location is { } location)
                    {
                        context.Response.Headers.Location = location.AbsoluteUri;
                    }

                    context.Response.ContentType = "application/json";
                    await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
                });
                await app.StartAsync();
                var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                return new TlsIssuer(app, certificate, new Uri(address).Port);
            }
            catch
            {
                if (app is not null)
                {
                    await app.DisposeAsync();
                }

                certificate.Dispose();
                throw;
            }
        }

        // Every ts-auth host name resolves to this listener.
        private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, _port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }
}
