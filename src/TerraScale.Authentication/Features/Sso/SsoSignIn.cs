using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;
using ZLogger;

namespace TerraScale.Authentication.Features.Sso;

public sealed record SsoState(string State, string Nonce, string Verifier, string Domain, string Issuer, string? Next, DateTimeOffset ExpiresAt, string OrganizationId, DateTimeOffset ConnectionUpdatedAt);

/// <summary>
/// Platform sign-in through an organization's OpenID Connect identity provider: authorization code with PKCE, state and
/// nonce, the ID token checked against the issuer's keys, and its email in the connection's verified domain. A first
/// sign-in creates the account and joins the organization with the connection's default role.
/// </summary>
public sealed class SsoSignIn(AuthData data, SsoConnections connections, Users users, PlatformDirectory platform, AuthOptions options, IHttpClientFactory clients, TimeProvider clock, ILogger<SsoSignIn> logger)
{
    /// <summary>The page that starts an SSO sign-in.</summary>
    public const string StartPath = $"/ui/{OidcIssuer.Slug}/sso";

    /// <summary>The redirect URI identity providers return to.</summary>
    public const string CallbackPath = SsoConnections.CallbackPath;

    /// <summary>The pending sign-in's cookie, scoped to the SSO pages.</summary>
    public const string Cookie = "tsauth_sso";

    /// <summary>How long a started sign-in may take at the identity provider.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private const string StateContext = "ts-auth org sso state";
    private static readonly JsonWebTokenHandler Handler = new();

    /// <summary>The SSO start page for <paramref name="email"/>, resuming <paramref name="next"/>.</summary>
    public static string StartUrl(string email, string? next) =>
        QueryHelpers.AddQueryString(StartPath, next is null ? new Dictionary<string, string?> { ["email"] = email } : new Dictionary<string, string?> { ["email"] = email, ["next"] = next });

    /// <summary>
    /// Whether <paramref name="email"/> must sign in through its organization's identity provider: its domain is verified
    /// and it is no owner of that organization (owners keep email-code and password sign-in to recover a broken connection).
    /// </summary>
    public async Task<bool> RequiredAsync(string? email, CancellationToken cancellationToken) =>
        await connections.EnforcedAsync(email, cancellationToken).ConfigureAwait(false) is { } connection
        && (await users.FindByEmailAsync(PlatformDirectory.Instance, email!, cancellationToken).ConfigureAwait(false) is not { } user
            || await platform.RoleAsync(connection.OrganizationId, user.Id, cancellationToken).ConfigureAwait(false) != Roles.Owner);

    /// <summary>The identity provider's authorization URL for <paramref name="email"/>, and the sealed state to set; null without an enforced connection.</summary>
    public async Task<(string Location, string State)?> StartAsync(string email, string? next, CancellationToken cancellationToken)
    {
        if (await connections.EnforcedAsync(email, cancellationToken).ConfigureAwait(false) is not { Protocol: SsoConnections.Oidc, Issuer: { } issuer } connection
            || await connections.DiscoverAsync(issuer, cancellationToken).ConfigureAwait(false) is not { } discovery)
        {
            return null;
        }

        var state = new SsoState(Tokens.RandomToken(43), Tokens.RandomToken(43), Tokens.RandomToken(64), connection.Domain, issuer, next, clock.GetUtcNow() + Lifetime, connection.OrganizationId, connection.UpdatedAt);
        var location = QueryHelpers.AddQueryString(discovery.AuthorizationEndpoint, new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = connection.ClientId,
            ["redirect_uri"] = this.RedirectUri,
            ["scope"] = "openid email profile",
            ["state"] = state.State,
            ["nonce"] = state.Nonce,
            ["code_challenge"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(state.Verifier))),
            ["code_challenge_method"] = "S256",
            ["login_hint"] = email,
        });
        return (location, SecretBox.Seal(options.MasterKey, JsonSerializer.SerializeToUtf8Bytes(state, SsoStateJson.Default.SsoState), StateContext));
    }

    /// <summary>The pending sign-in sealed in <paramref name="cookie"/> when it is unexpired and matches <paramref name="state"/>.</summary>
    public SsoState? Pending(string? cookie, string? state)
    {
        try
        {
            return cookie is { Length: > 0 and <= 4096 } && state is { Length: > 0 }
                && JsonSerializer.Deserialize(SecretBox.Open(options.MasterKey, cookie, StateContext), SsoStateJson.Default.SsoState) is { } pending
                && pending.ExpiresAt > clock.GetUtcNow() && Tokens.FixedTimeEquals(pending.State, state)
                    ? pending
                    : null;
        }
        catch (Exception error) when (error is CryptographicException or FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    public async Task<SsoCompletion?> CompleteAsync(SsoState pending, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (await connections.ForDomainAsync(pending.Domain, cancellationToken).ConfigureAwait(false) is not { Value: { VerifiedAt: not null, Protocol: SsoConnections.Oidc } connection }
            || connection.Issuer != pending.Issuer || connection.OrganizationId != pending.OrganizationId || connection.UpdatedAt != pending.ConnectionUpdatedAt
            || await platform.OrganizationAsync(connection.OrganizationId, cancellationToken).ConfigureAwait(false) is null
            || await connections.DiscoverAsync(pending.Issuer, cancellationToken).ConfigureAwait(false) is not { } discovery)
        {
            return null;
        }

        string? idToken;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, discovery.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = this.RedirectUri,
                    ["code_verifier"] = pending.Verifier,
                }),
            };
            // RFC 6749 §2.3.1: the client id and secret are form-encoded before Basic encoding.
            request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{Uri.EscapeDataString(connection.ClientId!)}:{Uri.EscapeDataString(connections.ClientSecret(connection))}")));
            using var response = await clients.CreateClient(SsoConnections.HttpClientName).SendAsync(request, cancellationToken).ConfigureAwait(false);
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            idToken = response.IsSuccessStatusCode && body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("id_token", out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.ZLogWarning($"Audit: SSO token request for {connection.Domain:@Domain} failed ({error.GetType().Name})");
            return null;
        }

        var result = idToken is null ? null : await Handler.ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = connection.Issuer,
            ValidAudience = connection.ClientId,
            ConfigurationManager = connections.Discovery(connection.Issuer!),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512, SecurityAlgorithms.RsaSsaPssSha256,
                SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512, SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512],
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            LifetimeValidator = (notBefore, expires, _, _) => expires > clock.GetUtcNow().UtcDateTime.AddMinutes(-1) && (notBefore is null || notBefore < clock.GetUtcNow().UtcDateTime.AddMinutes(1)),
        }).ConfigureAwait(false);
        var claims = result is { IsValid: true } ? result.Claims : null;
        string? Text(string name) => claims is not null && claims.TryGetValue(name, out var value) ? value as string : null;
        var verified = claims is not null && claims.TryGetValue("email_verified", out var flag) && (flag is true || flag is string text && text == "true");
        if (claims is null || Text("nonce") is not { } nonce || !Tokens.FixedTimeEquals(nonce, pending.Nonce)
            || Text("sub") is not { Length: > 0 and <= 1024 } subject
            || Text("azp") is { } azp && azp != connection.ClientId || !verified
            || Users.NormalizeEmail(Text("email")) is not { } email || SsoConnections.EmailDomain(email) != connection.Domain)
        {
            logger.ZLogWarning($"Audit: SSO sign-in for {connection.Domain:@Domain} refused: the ID token failed validation");
            return null;
        }

        var user = await users.FindByEmailAsync(PlatformDirectory.Instance, email, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            var found = await connections.ForDomainAsync(connection.Domain, cancellationToken).ConfigureAwait(false);
            var organization = await platform.OrganizationAsync(connection.OrganizationId, cancellationToken).ConfigureAwait(false);
            if (found is null || found.Value != connection || organization is null)
            {
                return null;
            }

            var account = users.NewAccount(PlatformDirectory.Instance, email, null, AuthIds.New());
            await platform.FenceAsync(connection.OrganizationId, null, null, cancellationToken).ConfigureAwait(false);
            var link = Link(account.User, connection, subject);
            if (await data.SaveAsync(
                [.. account.Changes, Change.Assert(found), Change.Assert(organization),
                    Change.Insert(new MembershipRecord(PlatformDirectory.Instance, connection.OrganizationId, account.User.Id, connection.DefaultRole, clock.GetUtcNow())),
                    Change.Insert(link), platform.Audit(connection.OrganizationId, account.User.Id, "member.joined_sso", account.User.Id, connection.Domain)],
                cancellationToken).ConfigureAwait(false))
            {
                logger.ZLogInformation($"Audit: user {account.User.Id:@UserId} joined organization {connection.OrganizationId:@OrganizationId} through SSO");
                user = await users.GetAsync(PlatformDirectory.Instance, account.User.Id, cancellationToken).ConfigureAwait(false)
                    ?? throw ApiException.Unavailable();
                return await this.LinkedAsync(user, connection, subject, cancellationToken).ConfigureAwait(false) is { } createdLink
                    ? new SsoLinked(user with { InboxProven = true }, subject, createdLink.Version) : null;
            }

            user = await users.FindByEmailAsync(PlatformDirectory.Instance, email, cancellationToken).ConfigureAwait(false);
        }

        if (user is null || user.Disabled)
        {
            return null;
        }

        return await this.LinkedAsync(user, connection, subject, cancellationToken).ConfigureAwait(false) is { } heldLink
            ? new SsoLinked(user with { InboxProven = true }, subject, heldLink.Version)
            : new SsoUnlinked(user, subject);
    }

    internal const string ProofCookie = "tsauth_sso_proof";

    public const string LinkPath = $"/ui/{OidcIssuer.Slug}/sso/link";

    internal void BeginProof(HttpContext context, SsoCompletion completed, SsoState pending)
    {
        var user = completed.User;
        this.WriteProof(context, new SsoProof(user.Id, user.Email, user.AuthEpoch, user.AuthStamp, pending.Domain, pending.Issuer,
            completed.Subject, completed.Match<string?>(linked => linked.LinkVersion, _ => null), pending.Next, clock.GetUtcNow() + Lifetime,
            pending.OrganizationId, pending.ConnectionUpdatedAt, false));
    }

    internal async Task<IResult> LinkPromptAsync(HttpContext context, SsoUnlinked unlinked, SsoState pending, CancellationToken cancellationToken) =>
        await platform.OrganizationAsync(pending.OrganizationId, cancellationToken).ConfigureAwait(false) is { } organization
            ? SignInPageModel.Render(context, new SignInPageModel(SignInStep.SsoLink, PlatformDirectory.Instance, unlinked.User.Email, pending.Next)
            {
                SsoOrganization = organization.Value.Name,
                SsoIssuer = new Uri(pending.Issuer).Host,
            })
            : SignInFlow.Refused(context);

    internal SsoProof? Confirm(HttpContext context)
    {
        if (this.Proof(context) is not { LinkVersion: null, Confirmed: false } proof)
        {
            return null;
        }

        var confirmed = proof with { Confirmed = true };
        this.WriteProof(context, confirmed);
        return confirmed;
    }

    private void WriteProof(HttpContext context, SsoProof proof)
    {
        context.Items[ProofCookie] = proof;
        var cookieOptions = ProofOptions;
        cookieOptions.MaxAge = proof.ExpiresAt - clock.GetUtcNow();
        context.Response.Cookies.Append(ProofCookie,
            SecretBox.Seal(options.MasterKey, JsonSerializer.SerializeToUtf8Bytes(proof, SsoStateJson.Default.SsoProof), ProofCookie), cookieOptions);
    }

    private SsoProof? Proof(HttpContext context)
    {
        if (context.Items.TryGetValue(ProofCookie, out var held))
        {
            return held as SsoProof;
        }

        SsoProof? proof;
        try
        {
            proof = context.Request.Cookies[ProofCookie] is { Length: > 0 and <= 4096 } value
                ? JsonSerializer.Deserialize(SecretBox.Open(options.MasterKey, value, ProofCookie), SsoStateJson.Default.SsoProof) : null;
        }
        catch (Exception error) when (error is CryptographicException or FormatException or JsonException or ArgumentException)
        {
            proof = null;
        }

        proof = proof is { Subject.Length: > 0 } && proof.ExpiresAt > clock.GetUtcNow() ? proof : null;
        context.Items[ProofCookie] = proof;
        return proof;
    }

    private static void Clear(HttpContext context)
    {
        if (context.Items.TryGetValue(ProofCookie, out var held) && held is not null || context.Request.Cookies.ContainsKey(ProofCookie))
        {
            context.Response.Cookies.Delete(ProofCookie, ProofOptions);
        }

        context.Items[ProofCookie] = null;
    }

    internal SsoProof? ProofFor(HttpContext context, string? userId, long? epoch, string? stamp)
    {
        var proof = this.Proof(context);
        if (proof is null)
        {
            return null;
        }

        if (SignInFlow.Tenant(context) == PlatformDirectory.Instance
            && context.Request.Path.StartsWithSegments($"/ui/{OidcIssuer.Slug}")
            && (proof.LinkVersion is not null || proof.Confirmed)
            && proof.UserId == userId && proof.AuthEpoch == epoch && proof.AuthStamp == stamp)
        {
            return proof;
        }

        Clear(context);
        return null;
    }

    internal async Task<bool> ContinuesAsync(HttpContext context, string email, CancellationToken cancellationToken)
    {
        if (this.Proof(context) is null)
        {
            return false;
        }

        var user = await users.FindByEmailAsync(PlatformDirectory.Instance, email, cancellationToken).ConfigureAwait(false);
        return this.ProofFor(context, user?.Id, user?.AuthEpoch, user?.AuthStamp) is not null;
    }

    internal string? FactorProofFor(HttpContext context, UserRecord user) =>
        this.ProofFor(context, user.Id, user.AuthEpoch, user.AuthStamp) is { } proof
            ? Tokens.Sha256Hex(JsonSerializer.Serialize(proof, SsoStateJson.Default.SsoProof))
            : null;

    internal void AdvanceEnrollment(HttpContext context, UserRecord previous, UserRecord enrolled)
    {
        if (this.ProofFor(context, previous.Id, previous.AuthEpoch, previous.AuthStamp) is { } proof)
        {
            this.WriteProof(context, proof with { AuthEpoch = enrolled.AuthEpoch, AuthStamp = enrolled.AuthStamp });
        }
    }

    internal async Task<bool> RequiresMfaAsync(HttpContext context, UserRecord user, CancellationToken cancellationToken) =>
        this.ProofFor(context, user.Id, user.AuthEpoch, user.AuthStamp) is { } proof
        && await platform.OrganizationAsync(proof.OrganizationId, cancellationToken).ConfigureAwait(false) is { Value.RequireMfa: true };

    internal async Task<bool> CanFinishAsync(HttpContext context, UserRecord user, CancellationToken cancellationToken) =>
        this.ProofFor(context, user.Id, user.AuthEpoch, user.AuthStamp) is { } proof
        && await this.PlanAsync(proof, true, cancellationToken).ConfigureAwait(false) is not null;

    internal async Task<bool> FinishProofAsync(HttpContext context, IssuedSession session, CancellationToken cancellationToken)
    {
        var proof = this.ProofFor(context, session.Session.UserId, session.Session.AuthEpoch, session.Session.AuthStamp);
        Clear(context);
        if (session.Session.InstanceId != PlatformDirectory.Instance || SignInFlow.Tenant(context) != PlatformDirectory.Instance
            || session.Session.Bindings.Any(binding => binding.Kind == "credential:impersonation-ticket")
            || proof is null
            || await this.PlanAsync(proof, session.Session.Mfa, cancellationToken).ConfigureAwait(false) is not { } plan
            || !await data.SaveAsync(plan.Changes, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (plan.Joining)
        {
            logger.ZLogInformation($"Audit: user {proof.UserId:@UserId} joined organization {proof.OrganizationId:@OrganizationId} through SSO");
        }

        return true;
    }

    private async Task<SsoLinkPlan?> PlanAsync(SsoProof proof, bool mfa, CancellationToken cancellationToken)
    {
        if (await connections.ForDomainAsync(proof.Domain, cancellationToken).ConfigureAwait(false) is not { Value: { VerifiedAt: not null } connection } found
            || connection.Issuer != proof.Issuer || connection.OrganizationId != proof.OrganizationId || connection.UpdatedAt != proof.ConnectionUpdatedAt
            || await platform.OrganizationAsync(connection.OrganizationId, cancellationToken).ConfigureAwait(false) is not { } organization
            || organization.Value.RequireMfa && !mfa
            || await users.GetAsync(PlatformDirectory.Instance, proof.UserId, cancellationToken).ConfigureAwait(false) is not { Disabled: false } user
            || user.AuthEpoch != proof.AuthEpoch || user.AuthStamp != proof.AuthStamp)
        {
            return null;
        }

        var joining = await platform.RoleAsync(connection.OrganizationId, user.Id, cancellationToken).ConfigureAwait(false) is null;
        List<Change>? changes = joining
            ? await platform.JoinAsync(connection.OrganizationId, user.Id, connection.DefaultRole, cancellationToken).ConfigureAwait(false)
            : [Change.Assert(user), Change.Assert(organization)];
        if (changes is null)
        {
            return null;
        }

        var linked = await data.ReadAsync((db, token) => db.SsoIdentityLinks.Where(link => link.Domain == proof.Domain && link.Issuer == proof.Issuer
            && link.Subject == proof.Subject).WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);
        if (linked is not null && linked.Value.UserId != user.Id
            || proof.LinkVersion is { } expected && (linked is null || linked.Version != expected))
        {
            return null;
        }

        changes.Add(Change.Assert(found));
        changes.Add(Change.Assert(organization));
        var record = Link(user, connection, proof.Subject);
        changes.Add(linked is null ? Change.Insert(record)
            : linked.Value == record ? Change.Assert(linked) : Change.Update(linked, record));
        if (joining)
        {
            changes.Add(platform.Audit(connection.OrganizationId, user.Id, "member.joined_sso", user.Id, connection.Domain));
        }

        return new(changes, joining);
    }

    private Task<Versioned<SsoIdentityLinkRecord>?> LinkedAsync(UserRecord user, SsoConnectionRecord connection, string subject, CancellationToken cancellationToken) =>
        data.ReadAsync((db, token) => db.SsoIdentityLinks.Where(link => link.Domain == connection.Domain && link.OrganizationId == connection.OrganizationId
            && link.Issuer == connection.Issuer && link.Subject == subject && link.UserId == user.Id && link.ConnectionUpdatedAt == connection.UpdatedAt)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken);

    private static SsoIdentityLinkRecord Link(UserRecord user, SsoConnectionRecord connection, string subject) =>
        new(connection.Domain, connection.OrganizationId, connection.Issuer!, subject, user.Id, connection.UpdatedAt);

    private static CookieOptions ProofOptions => new()
    {
        Path = $"/ui/{OidcIssuer.Slug}",
        Secure = true,
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        MaxAge = Lifetime,
        IsEssential = true,
    };

    private string RedirectUri => new Uri(options.PublicOrigin, CallbackPath).AbsoluteUri;
}

public abstract record SsoCompletion
{
    private protected SsoCompletion(UserRecord user, string subject)
    {
        this.User = user;
        this.Subject = subject;
    }

    public UserRecord User { get; }

    public string Subject { get; }

    public abstract T Match<T>(Func<SsoLinked, T> linked, Func<SsoUnlinked, T> unlinked);
}

public sealed record SsoLinked(UserRecord User, string Subject, string LinkVersion) : SsoCompletion(User, Subject)
{
    public override T Match<T>(Func<SsoLinked, T> linked, Func<SsoUnlinked, T> unlinked) => linked(this);
}

public sealed record SsoUnlinked(UserRecord User, string Subject) : SsoCompletion(User, Subject)
{
    public override T Match<T>(Func<SsoLinked, T> linked, Func<SsoUnlinked, T> unlinked) => unlinked(this);
}

public sealed record SsoIdentityLinkRecord(string Domain, string OrganizationId, string Issuer, string Subject, string UserId, DateTimeOffset ConnectionUpdatedAt);

internal sealed record SsoProof(string UserId, string Email, long AuthEpoch, string? AuthStamp, string Domain, string Issuer, string Subject, string? LinkVersion,
    string? Next, DateTimeOffset ExpiresAt, string OrganizationId, DateTimeOffset ConnectionUpdatedAt, bool Confirmed);

internal sealed record SsoLinkPlan(IReadOnlyList<Change> Changes, bool Joining);

[JsonSerializable(typeof(SsoState))]
[JsonSerializable(typeof(SsoProof))]
internal sealed partial class SsoStateJson : JsonSerializerContext;
