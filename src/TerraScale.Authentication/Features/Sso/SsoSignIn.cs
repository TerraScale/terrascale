using Dapper;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
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

public sealed class SsoSignIn(AuthData data, SsoConnections connections, Users users, PlatformDirectory platform, AuthOptions options, IHttpClientFactory clients, TimeProvider clock, ILogger<SsoSignIn> logger)
{
    public const string StartPath = $"/ui/{OidcIssuer.Slug}/sso";

    public const string CallbackPath = SsoConnections.CallbackPath;

    public const string Cookie = "tsauth_sso";

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private const string StateContext = "ts-auth org sso state";
    private static readonly JsonWebTokenHandler Handler = new();

    public static string StartUrl(string email, string? next) =>
        QueryHelpers.AddQueryString(StartPath, next is null ? new Dictionary<string, string?> { ["email"] = email } : new Dictionary<string, string?> { ["email"] = email, ["next"] = next });

    public async Task<bool> RequiredAsync(string? email, CancellationToken cancellationToken) =>
        await connections.EnforcedAsync(email, cancellationToken) is { } connection
        && (await users.FindByEmailAsync(PlatformDirectory.Instance, email!, cancellationToken) is not { } user
            || await platform.RoleAsync(connection.OrganizationId, user.Id, cancellationToken) != Roles.Owner);

    public async Task<(string Location, string State)?> StartAsync(string email, string? next, CancellationToken cancellationToken)
    {
        if (await connections.EnforcedAsync(email, cancellationToken) is not { Protocol: SsoConnections.Oidc, Issuer: { } issuer } connection
            || await connections.DiscoverAsync(issuer, cancellationToken) is not { } discovery)
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
        if (await connections.ForDomainAsync(pending.Domain, cancellationToken) is not { Value: { VerifiedAt: not null, Protocol: SsoConnections.Oidc } connection }
            || connection.Issuer != pending.Issuer || connection.OrganizationId != pending.OrganizationId || connection.UpdatedAt != pending.ConnectionUpdatedAt
            || await platform.OrganizationAsync(connection.OrganizationId, cancellationToken) is null
            || await connections.DiscoverAsync(pending.Issuer, cancellationToken) is not { } discovery)
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
            request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{Uri.EscapeDataString(connection.ClientId!)}:{Uri.EscapeDataString(connections.ClientSecret(connection))}")));
            using var response = await clients.CreateClient(SsoConnections.HttpClientName).SendAsync(request, cancellationToken);
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
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
        });
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

        var user = await users.FindByEmailAsync(PlatformDirectory.Instance, email, cancellationToken);
        if (user is null)
        {
            var found = await connections.ForDomainAsync(connection.Domain, cancellationToken);
            var organization = await platform.OrganizationAsync(connection.OrganizationId, cancellationToken);
            if (found is null || found.Value != connection || organization is null)
            {
                return null;
            }

            var account = users.NewAccount(PlatformDirectory.Instance, email, null, AuthIds.New());
            await platform.FenceAsync(connection.OrganizationId, null, null, cancellationToken);
            var link = Link(account.User, connection, subject);
            if (await data.SaveAsync(
                [.. account.Changes, Change.Assert(found), Change.Assert(organization),
                    Change.Insert(new MembershipRecord(PlatformDirectory.Instance, connection.OrganizationId, account.User.Id, connection.DefaultRole, clock.GetUtcNow())),
                    Change.Insert(link), platform.Audit(connection.OrganizationId, account.User.Id, "member.joined_sso", account.User.Id, connection.Domain)],
                cancellationToken))
            {
                logger.ZLogInformation($"Audit: user {account.User.Id:@UserId} joined organization {connection.OrganizationId:@OrganizationId} through SSO");
                user = await users.GetAsync(PlatformDirectory.Instance, account.User.Id, cancellationToken)
                    ?? throw ApiException.Unavailable();
                return await this.LinkedAsync(user, connection, subject, cancellationToken) is { } createdLink
                    ? new SsoLinked(user with { InboxProven = true }, subject, createdLink.Version) : null;
            }

            user = await users.FindByEmailAsync(PlatformDirectory.Instance, email, cancellationToken);
        }

        if (user is null || user.Disabled)
        {
            return null;
        }

        return await this.LinkedAsync(user, connection, subject, cancellationToken) is { } heldLink
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

    internal async Task<IResult> LinkPromptAsync(HttpContext context, CancellationToken cancellationToken) =>
        this.Proof(context) is { LinkVersion: null, Confirmed: false } proof
        && await platform.OrganizationAsync(proof.OrganizationId, cancellationToken) is { } organization
            ? SignInPageModel.Render(context, new SignInPageModel(SignInStep.SsoLink, PlatformDirectory.Instance, proof.Email, proof.Next)
            {
                SsoOrganization = DisplayName(organization.Value.Name),
                SsoIssuer = proof.Issuer,
                SsoConfirmation = Digest(proof),
            })
            : SignInFlow.Refused(context);

    internal SsoProof? Confirm(HttpContext context, string? confirmation)
    {
        if (this.Proof(context) is not { LinkVersion: null, Confirmed: false } proof
            || confirmation is not { Length: 64 } || !Tokens.FixedTimeEquals(Digest(proof), confirmation))
        {
            return null;
        }

        var confirmed = proof with { Confirmed = true };
        this.WriteProof(context, confirmed);
        return confirmed;
    }

    private static string Digest(SsoProof proof) => Tokens.Sha256Hex(JsonSerializer.Serialize(proof, SsoStateJson.Default.SsoProof));

    private static string DisplayName(string name) => string.Concat(name.EnumerateRunes()
        .Where(rune => Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.Format))
        .Select(rune => rune.ToString()));

    internal bool HasProof(HttpContext context) => this.Proof(context) is { LinkVersion: not null } or { Confirmed: true };

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
        if (proof is null)
        {
            Clear(context);
        }

        return proof;
    }

    internal static void Clear(HttpContext context)
    {
        if (context.Items.TryGetValue(ProofCookie, out var held) && held is not null || context.Request.Cookies.ContainsKey(ProofCookie))
        {
            context.Response.Cookies.Delete(ProofCookie, ProofOptions);
            context.Response.OnStarting(() =>
            {
                if (context.Items[ProofCookie] is null)
                {
                    context.Response.Cookies.Delete(ProofCookie, ProofOptions);
                }

                return Task.CompletedTask;
            });
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

        var user = await users.FindByEmailAsync(PlatformDirectory.Instance, email, cancellationToken);
        return this.ProofFor(context, user?.Id, user?.AuthEpoch, user?.AuthStamp) is not null;
    }

    internal string? FactorProofFor(HttpContext context, UserRecord user) =>
        this.ProofFor(context, user.Id, user.AuthEpoch, user.AuthStamp) is { } proof
            ? Digest(proof)
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
        && await platform.OrganizationAsync(proof.OrganizationId, cancellationToken) is { Value.RequireMfa: true };

    internal async Task<bool> CanFinishAsync(HttpContext context, UserRecord user, CancellationToken cancellationToken) =>
        this.ProofFor(context, user.Id, user.AuthEpoch, user.AuthStamp) is { } proof
        && await this.PlanAsync(proof, true, cancellationToken) is not null;

    internal async Task<IssuedSession?> FinishProofAsync(HttpContext context, Sessions sessions, IssuedSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var proof = this.ProofFor(context, session.Session.UserId, session.Session.AuthEpoch, session.Session.AuthStamp);
        Clear(context);
        if (session.Session.InstanceId != PlatformDirectory.Instance || SignInFlow.Tenant(context) != PlatformDirectory.Instance
            || session.Session.Bindings.Any(binding => binding.Kind == "credential:impersonation-ticket")
            || proof is null
            || await this.PlanAsync(proof, session.Session.Mfa, cancellationToken) is not { } plan
            || !await data.SaveAsync(plan.Changes, cancellationToken))
        {
            return null;
        }

        if (plan.Joining)
        {
            logger.ZLogInformation($"Audit: user {proof.UserId:@UserId} joined organization {proof.OrganizationId:@OrganizationId} through SSO");
        }

        if (plan.Advanced is not { } advanced)
        {
            return session;
        }

        logger.ZLogInformation($"Audit: user {proof.UserId:@UserId} linked a new SSO identity; every earlier session and token was revoked");
        await sessions.RevokeAsync(session.Session.InstanceId, session.Token, cancellationToken);
        return await SignInFlow.OpenSessionAsync(sessions,
            Users.BindAuthenticationState(advanced) with { MultiFactor = session.Session.Mfa, MfaMethod = session.Session.MfaMethod }, cancellationToken);
    }

    private async Task<SsoLinkPlan?> PlanAsync(SsoProof proof, bool mfa, CancellationToken cancellationToken)
    {
        if (await connections.ForDomainAsync(proof.Domain, cancellationToken) is not { Value: { VerifiedAt: not null } connection } found
            || connection.Issuer != proof.Issuer || connection.OrganizationId != proof.OrganizationId || connection.UpdatedAt != proof.ConnectionUpdatedAt
            || await platform.OrganizationAsync(connection.OrganizationId, cancellationToken) is not { } organization
            || organization.Value.RequireMfa && !mfa
            || await users.GetAsync(PlatformDirectory.Instance, proof.UserId, cancellationToken) is not { Disabled: false } user
            || user.AuthEpoch != proof.AuthEpoch || user.AuthStamp != proof.AuthStamp)
        {
            return null;
        }

        var joining = await platform.RoleAsync(connection.OrganizationId, user.Id, cancellationToken) is null;
        List<Change>? changes = joining
            ? await platform.JoinAsync(connection.OrganizationId, user.Id, connection.DefaultRole, cancellationToken)
            : [];
        if (changes is null)
        {
            return null;
        }

        var (domain, issuer, subject) = (proof.Domain, proof.Issuer, proof.Subject);
        var linked = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<SsoIdentityLinkRecord>(
            "SELECT * FROM sso_identity_links WHERE domain = @domain AND issuer = @issuer AND subject = @subject", new { domain, issuer, subject }), cancellationToken));
        if (linked is not null && linked.Value.UserId != user.Id
            || proof.LinkVersion is { } expected && (linked is null || linked.Version != expected))
        {
            return null;
        }

        var advanced = linked is null ? user with { AuthEpoch = checked(user.AuthEpoch + 1), AuthStamp = AuthIds.New() } : null;
        var userChange = advanced is null ? Change.Assert(user) : Change.Update(advanced);
        changes.Add(userChange);
        changes.Add(Change.Assert(found));
        changes.Add(Change.Assert(organization));
        var record = Link(user, connection, proof.Subject);
        changes.Add(linked is null ? Change.Insert(record)
            : linked.Value == record ? Change.Assert(linked) : Change.Update(linked, record));
        if (joining)
        {
            changes.Add(platform.Audit(connection.OrganizationId, user.Id, "member.joined_sso", user.Id, connection.Domain));
        }

        return new(changes, joining, advanced is null ? null : advanced with { Version = userChange.Version });
    }

    private async Task<Versioned<SsoIdentityLinkRecord>?> LinkedAsync(UserRecord user, SsoConnectionRecord connection, string subject, CancellationToken cancellationToken)
    {
        var (domain, organizationId, issuer, userId, updatedAt) = (connection.Domain, connection.OrganizationId, connection.Issuer, user.Id, connection.UpdatedAt);
        return Versioned.Of(await data.ReadAsync(store => store.QuerySingleOrDefaultAsync<SsoIdentityLinkRecord>(
            """
            SELECT * FROM sso_identity_links WHERE domain = @domain AND organization_id = @organizationId AND issuer IS @issuer AND subject = @subject
                AND user_id = @userId AND connection_updated_at = @updatedAt
            """, new { domain, organizationId, issuer, subject, userId, updatedAt }), cancellationToken));
    }

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

public sealed record SsoIdentityLinkRecord(string Domain, string OrganizationId, string Issuer, string Subject, string UserId, DateTimeOffset ConnectionUpdatedAt)
{
    public string? Version { get; init; }
}

internal sealed record SsoProof(string UserId, string Email, long AuthEpoch, string? AuthStamp, string Domain, string Issuer, string Subject, string? LinkVersion,
    string? Next, DateTimeOffset ExpiresAt, string OrganizationId, DateTimeOffset ConnectionUpdatedAt, bool Confirmed);

internal sealed record SsoLinkPlan(IReadOnlyList<Change> Changes, bool Joining, UserRecord? Advanced);

[JsonSerializable(typeof(SsoState))]
[JsonSerializable(typeof(SsoProof))]
internal sealed partial class SsoStateJson : JsonSerializerContext;
