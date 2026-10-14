using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Tests;

/// <summary>
/// An in-process stand-in for GitHub Actions' OIDC issuer: its discovery document and JWKS, served through a message
/// handler that replaces ts-auth's <see cref="GitHubActionsGrant.HttpClientName"/> transport (nothing reaches GitHub), and
/// tokens signed with its RS256 key carrying the trust policy's claims unless a test overrides them.
/// </summary>
public sealed class FakeGitHubActions : IDisposable
{
    private static readonly JsonWebTokenHandler Tokens = new() { SetDefaultTimesOnTokenCreation = false };
    private readonly RSA rsa = RSA.Create(2048);
    private readonly RsaSecurityKey key;

    public FakeGitHubActions()
    {
        this.key = new RsaSecurityKey(this.rsa) { KeyId = Guid.NewGuid().ToString("N") };
    }

    /// <summary>Routes ts-auth's GitHub OIDC client to this issuer.</summary>
    public void Route(IServiceCollection services) =>
        services.AddHttpClient(GitHubActionsGrant.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new Handler(this));

    /// <summary>The <c>kid</c> of the published key.</summary>
    public string KeyId => this.key.KeyId;

    /// <summary>Credentials that sign with the published key under <paramref name="algorithm"/> (RS256 is what GitHub uses).</summary>
    public SigningCredentials Signing(string algorithm) => new(this.key, algorithm);

    /// <summary>
    /// A GitHub OIDC token for the e2e job on a push to main, for <paramref name="audience"/> (none when null).
    /// <paramref name="claims"/> replaces or (with a null value) removes claims; <paramref name="signing"/> signs instead
    /// of the published key; <paramref name="issuer"/> replaces GitHub's issuer; <paramref name="notBeforeIn"/> moves
    /// <c>nbf</c>; without <paramref name="expires"/> the token has no <c>exp</c>; <paramref name="json"/> replaces claims
    /// with raw JSON values (JSON null, numbers, arrays), which <paramref name="claims"/> cannot express.
    /// </summary>
    public string Mint(string? audience, IReadOnlyDictionary<string, string?>? claims = null, TimeSpan? expiresIn = null, SigningCredentials? signing = null,
        string issuer = GitHubActionsGrant.Issuer, TimeSpan? notBeforeIn = null, bool expires = true, IReadOnlyDictionary<string, JsonElement>? json = null)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["jti"] = Guid.NewGuid().ToString(),
            ["sub"] = "repo:TerraScale/terrascale:ref:refs/heads/main",
            ["repository_id"] = GitHubActionsGrant.RepositoryId,
            ["repository_owner_id"] = GitHubActionsGrant.RepositoryOwnerId,
            ["repository"] = GitHubActionsGrant.Repository,
            ["ref"] = GitHubActionsGrant.Ref,
            ["event_name"] = GitHubActionsGrant.EventName,
            ["workflow_ref"] = GitHubActionsGrant.WorkflowRef,
            ["job_workflow_ref"] = GitHubActionsGrant.WorkflowRef,
            ["run_id"] = "18000000001",
            ["run_attempt"] = "1",
            ["sha"] = "0123456789abcdef0123456789abcdef01234567",
        };
        foreach (var (name, value) in claims ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                payload.Remove(name);
            }
            else
            {
                payload[name] = value;
            }
        }

        foreach (var (name, value) in json ?? new Dictionary<string, JsonElement>())
        {
            payload[name] = value;
        }

        return Tokens.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = (now + (notBeforeIn ?? TimeSpan.Zero)).UtcDateTime,
            Expires = expires ? (now + (expiresIn ?? TimeSpan.FromMinutes(5))).UtcDateTime : null,
            Claims = payload,
            SigningCredentials = signing ?? new SigningCredentials(this.key, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <inheritdoc />
    public void Dispose() => this.rsa.Dispose();

    private static string Discovery() => new JsonObject
    {
        ["issuer"] = GitHubActionsGrant.Issuer,
        ["jwks_uri"] = GitHubActionsGrant.Issuer + "/.well-known/jwks",
    }.ToJsonString();

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

    private sealed class Handler(FakeGitHubActions issuer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri?.AbsoluteUri switch
            {
                GitHubActionsGrant.Issuer + "/.well-known/openid-configuration" => Discovery(),
                GitHubActionsGrant.Issuer + "/.well-known/jwks" => issuer.Jwks(),
                _ => null,
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
