using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Authentication.Features.Sso;

namespace TerraScale.Authentication.Tests;

/// <summary>
/// An in-process organization identity provider and DNS-over-HTTPS resolver, served through a message handler that
/// replaces ts-auth's <see cref="SsoConnections.HttpClientName"/> transport (nothing leaves the process): discovery,
/// JWKS, a token endpoint that redeems codes from <see cref="Authorize"/> for RS256 ID tokens, and TXT records.
/// </summary>
public sealed class FakeSsoIdp : IDisposable
{
    public const string Issuer = "https://idp.example.test/realms/acme";
    public const string ClientId = "terrascale";
    public const string ClientSecret = "fake-idp-client-secret";

    private static readonly JsonWebTokenHandler Tokens = new();
    private readonly RSA rsa = RSA.Create(2048);
    private readonly RsaSecurityKey key;
    private readonly ConcurrentDictionary<string, (string Email, string Nonce, string Challenge, IReadOnlyDictionary<string, object?> Claims)> codes = new();

    public FakeSsoIdp()
    {
        this.key = new RsaSecurityKey(this.rsa) { KeyId = Guid.NewGuid().ToString("N") };
    }

    /// <summary>TXT records by name.</summary>
    public ConcurrentDictionary<string, string> Txt { get; } = new(StringComparer.Ordinal);

    /// <summary>Routes ts-auth's SSO client to this provider.</summary>
    public void Route(IServiceCollection services) =>
        services.AddHttpClient(SsoConnections.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new Handler(this));

    /// <summary>
    /// What the provider does with the authorization request at <paramref name="location"/>: signs <paramref name="email"/>
    /// in and returns the code and state it redirects back with. <paramref name="claims"/> replaces or (null) removes claims.
    /// </summary>
    public (string Code, string State) Authorize(Uri location, string email, IReadOnlyDictionary<string, object?>? claims = null)
    {
        ArgumentNullException.ThrowIfNull(location);
        var query = QueryHelpers.ParseQuery(location.Query);
        var code = Guid.NewGuid().ToString("N");
        this.codes[code] = (email, query["nonce"].ToString(), query["code_challenge"].ToString(), claims ?? new Dictionary<string, object?>());
        return (code, query["state"].ToString());
    }

    /// <inheritdoc />
    public void Dispose() => this.rsa.Dispose();

    private string? Token(string body, string? authorization)
    {
        var form = QueryHelpers.ParseQuery(body);
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}"));
        if (authorization != expected || !this.codes.TryRemove(form["code"].ToString(), out var grant)
            || Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString()))) != grant.Challenge)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["iss"] = Issuer,
            ["aud"] = ClientId,
            ["sub"] = Guid.NewGuid().ToString(),
            ["email"] = grant.Email,
            ["email_verified"] = true,
            ["nonce"] = grant.Nonce,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = (now + TimeSpan.FromMinutes(5)).ToUnixTimeSeconds(),
        };
        foreach (var (name, value) in grant.Claims)
        {
            payload[name] = value;
        }

        var token = Tokens.CreateToken(JsonSerializer.Serialize(payload.Where(pair => pair.Value is not null).ToDictionary()), new SigningCredentials(this.key, SecurityAlgorithms.RsaSha256));
        return new JsonObject { ["id_token"] = token, ["access_token"] = "unused", ["token_type"] = "Bearer" }.ToJsonString();
    }

    private string Jwks()
    {
        var parameters = this.rsa.ExportParameters(includePrivateParameters: false);
        return new JsonObject
        {
            ["keys"] = new JsonArray(new JsonObject
            {
                ["kty"] = "RSA",
                ["alg"] = "RS256",
                ["use"] = "sig",
                ["kid"] = this.key.KeyId,
                ["n"] = Base64UrlEncoder.Encode(parameters.Modulus),
                ["e"] = Base64UrlEncoder.Encode(parameters.Exponent),
            }),
        }.ToJsonString();
    }

    private string Dns(Uri uri)
    {
        var name = QueryHelpers.ParseQuery(uri.Query)["name"].ToString();
        var answers = new JsonArray();
        if (this.Txt.TryGetValue(name, out var value))
        {
            answers.Add(new JsonObject { ["name"] = name, ["type"] = 16, ["TTL"] = 300, ["data"] = $"\"{value}\"" });
        }

        return new JsonObject { ["Status"] = 0, ["Answer"] = answers }.ToJsonString();
    }

    private sealed class Handler(FakeSsoIdp idp) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var body = uri.GetLeftPart(UriPartial.Path) switch
            {
                Issuer + "/.well-known/openid-configuration" => new JsonObject
                {
                    ["issuer"] = Issuer,
                    ["authorization_endpoint"] = Issuer + "/protocol/openid-connect/auth",
                    ["token_endpoint"] = Issuer + "/protocol/openid-connect/token",
                    ["jwks_uri"] = Issuer + "/protocol/openid-connect/certs",
                }.ToJsonString(),
                Issuer + "/protocol/openid-connect/certs" => idp.Jwks(),
                Issuer + "/protocol/openid-connect/token" when request.Method == HttpMethod.Post =>
                    idp.Token(await request.Content!.ReadAsStringAsync(cancellationToken), request.Headers.Authorization?.ToString()),
                SsoConnections.Resolver => idp.Dns(uri),
                _ => null,
            };
            return body is null
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_grant\"}", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
