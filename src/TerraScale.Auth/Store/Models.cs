using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TerraScale.Auth.Store;

/// <summary>A ts-auth customer instance (one tenant of the IdP).</summary>
public sealed record Instance
{
    public required string Id { get; init; }               // ins_...
    public required string Slug { get; init; }             // unique, url-safe
    public required string Name { get; init; }
    public string? LogoUrl { get; init; }                  // hosted UI branding
    public string? ThemeColor { get; init; }               // hosted UI accent, e.g. #6c5ce7
    public string? StripeCustomerId { get; init; }         // billing linkage
    public required long CreatedAt { get; init; }
}

/// <summary>An end user of an instance.</summary>
public sealed record User
{
    public required string Id { get; init; }               // usr_...
    public required string InstanceId { get; init; }
    public string? Email { get; init; }
    public bool EmailVerified { get; init; }
    public string? Phone { get; init; }
    public string? Username { get; init; }
    public string? PasswordHash { get; init; }             // PHC string, null when no password
    public string? Name { get; init; }
    public string? ImageUrl { get; init; }
    public bool Banned { get; init; }
    public string? BanReason { get; init; }
    public bool Deleted { get; init; }
    public Dictionary<string, object> PublicMetadata { get; init; } = [];
    public Dictionary<string, object> PrivateMetadata { get; init; } = [];
    public long CreatedAt { get; init; }
    public long UpdatedAt { get; init; }
    public long? LastSignInAt { get; init; }
    public string? EmailKey { get; init; }                 // instanceId:email — unique index target
    public string? UsernameKey { get; init; }              // instanceId:username — unique index target
}

/// <summary>A stored passkey credential.</summary>
public sealed record Passkey
{
    public required string Id { get; init; }               //pky_...
    public required string InstanceId { get; init; }
    public required string UserId { get; init; }
    public required string CredentialKey { get; init; }    // base64url(credentialId) — unique
    public required string PublicKeyCborBase64 { get; init; }
    public required string Algorithm { get; init; }        // ES256, RS256, PS256
    public required uint SignCount { get; init; }
    public string? Transports { get; init; }
    public required string Nickname { get; init; }
    public long CreatedAt { get; init; }
    public long? LastUsedAt { get; init; }
}

/// <summary>A sign-in session (server side); the cookie carries a random token whose hash is stored.</summary>
public sealed record Session
{
    public required string Id { get; init; }               // ses_...
    public required string InstanceId { get; init; }
    public required string UserId { get; init; }
    public required string TokenKey { get; init; }         // sha256(token) — unique
    public required string Status { get; init; }           // active | revoked
    public required long CreatedAt { get; init; }
    public required long ExpiresAt { get; init; }
    public long? LastActiveAt { get; init; }
    public string? UserAgent { get; init; }
    public string? IpAddress { get; init; }
}

/// <summary>An OAuth2/OIDC client application.</summary>
public sealed record OAuthClient
{
    public required string Id { get; init; }               // clt_...
    public required string InstanceId { get; init; }
    public required string ClientId { get; init; }
    public required string ClientKey { get; init; }        // instanceId:clientId — unique
    public required string Name { get; init; }
    public string? ClientSecretHash { get; init; }         // null for public clients
    public required string[] RedirectUris { get; init; }
    public string[] AllowedOrigins { get; init; } = [];
    public required string TokenEndpointAuthMethod { get; init; } // client_secret_basic | client_secret_post | none
    public bool Public { get; init; }
    public long CreatedAt { get; init; }
}

/// <summary>A consumed-once authorization code grant.</summary>
public sealed record AuthorizationCode
{
    public required string Id { get; init; }
    public required string InstanceId { get; init; }
    public required string ClientId { get; init; }
    public required string UserId { get; init; }
    public required string CodeKey { get; init; }          // sha256(code) — unique
    public required string RedirectUri { get; init; }
    public required string[] Scopes { get; init; }
    public string? CodeChallenge { get; init; }
    public string? CodeChallengeMethod { get; init; }      // S256
    public string? Nonce { get; init; }
    public required long ExpiresAt { get; init; }
    public bool Consumed { get; init; }
}

/// <summary>A rotating refresh token.</summary>
public sealed record RefreshToken
{
    public required string Id { get; init; }               // rtk_...
    public required string InstanceId { get; init; }
    public required string ClientId { get; init; }
    public required string UserId { get; init; }
    public required string TokenKey { get; init; }         // unique
    public required string Status { get; init; }           // active | rotated | revoked
    public required string[] Scopes { get; init; }
    public required long CreatedAt { get; init; }
    public required long ExpiresAt { get; init; }
    public string? RotatedTo { get; init; }
}

/// <summary>A management API key (sk_...); the secret is stored hashed.</summary>
public sealed record ApiKey
{
    public required string Id { get; init; }               // key_...
    public required string InstanceId { get; init; }
    public required string Name { get; init; }
    public required string SecretKey { get; init; }        // sha256(secret) — unique
    public required string[] Scopes { get; init; }
    public required long CreatedAt { get; init; }
    public long? LastUsedAt { get; init; }
    public bool Revoked { get; init; }
}

/// <summary>An MFA factor (TOTP) or recovery code set holder.</summary>
public sealed record MfaFactor
{
    public required string Id { get; init; }               // mfa_...
    public required string InstanceId { get; init; }
    public required string UserId { get; init; }
    public required string Type { get; init; }             // totp | recovery
    public string? SecretBox { get; init; }                // encrypted TOTP secret (v1:...)
    public string[]? RecoveryCodes { get; init; }          // hashed codes for recovery factors
    public bool Confirmed { get; init; }
    public required long CreatedAt { get; init; }
}

/// <summary>An organization inside an instance.</summary>
public sealed record Organization
{
    public required string Id { get; init; }               // org_...
    public required string InstanceId { get; init; }
    public required string Name { get; init; }
    public required string SlugKey { get; init; }          // instanceId:slug — unique
    public Dictionary<string, object> PublicMetadata { get; init; } = [];
    public long CreatedAt { get; init; }
}

/// <summary>A user's membership in an organization.</summary>
public sealed record OrgMembership
{
    public required string Id { get; init; }               // mem_...
    public required string InstanceId { get; init; }
    public required string OrgId { get; init; }
    public required string UserId { get; init; }
    public required string MemberKey { get; init; }        // orgId:userId — unique
    public required string Role { get; init; }             // admin | member | custom
    public long CreatedAt { get; init; }
}

/// <summary>A pending organization invitation.</summary>
public sealed record Invitation
{
    public required string Id { get; init; }               // inv_...
    public required string InstanceId { get; init; }
    public required string OrgId { get; init; }
    public required string Email { get; init; }
    public required string Role { get; init; }
    public required string Status { get; init; }           // pending | accepted | revoked
    public required long ExpiresAt { get; init; }
    public long CreatedAt { get; init; }
}

/// <summary>Single-use tokens: email verification, password reset, magic links, WebAuthn challenges.</summary>
public sealed record VerificationToken
{
    public required string Id { get; init; }
    public required string InstanceId { get; init; }
    public string? UserId { get; init; }
    public required string Purpose { get; init; }          // verify_email | reset_password | magic_link | webauthn.register | webauthn.assert | mfa.enroll
    public required string TokenKey { get; init; }         // unique
    public required long ExpiresAt { get; init; }
    public bool Consumed { get; init; }
    /// <summary>Durable consecutive invalid passcode attempts for this token.</summary>
    public int FailedAttempts { get; init; }
    public long CreatedAt { get; init; }
}

/// <summary>An outbound webhook endpoint for instance events.</summary>
public sealed record WebhookEndpoint
{
    public required string Id { get; init; }               // wh_...
    public required string InstanceId { get; init; }
    public required string Url { get; init; }
    public required string[] Events { get; init; }
    public required string SigningSecretBox { get; init; } // v1 AES-GCM box; plaintext is never persisted
    public bool Enabled { get; init; } = true;
    public long CreatedAt { get; init; }
}

/// <summary>Durable per-instance delivery checkpoint. It prevents a process
/// restart from silently skipping audit events that were queued after the last
/// successful dispatcher tick.</summary>
public sealed record WebhookCursor
{
    public required string Id { get; init; }
    public required string InstanceId { get; init; }
    public required long LastCreatedAt { get; init; }
    public required string LastEventId { get; init; }
    public required long UpdatedAt { get; init; }
}

/// <summary>Durable outcome for one endpoint and audit event.</summary>
public sealed record WebhookDelivery
{
    public required string Id { get; init; }
    public required string InstanceId { get; init; }
    public required string EndpointId { get; init; }
    public required string EventId { get; init; }
    public required string Status { get; init; } // pending | succeeded | dead
    public int Attempts { get; init; }
    public string? LastError { get; init; }
    public long UpdatedAt { get; init; }
}

/// <summary>An audit event (WorkOS-style audit log).</summary>
public sealed record AuditEvent
{
    public required string Id { get; init; }               // evt_...
    public required string InstanceId { get; init; }
    public required string Type { get; init; }
    public string? ActorId { get; init; }
    public Dictionary<string, object> Data { get; init; } = [];
    public long CreatedAt { get; init; }
    /// <summary>Versioned digest over the immutable event fields and canonical data.</summary>
    public int? IntegrityVersion { get; init; }
    public string? IntegrityHash { get; init; }
    /// <summary>HMAC-SHA256 over the versioned integrity hash, when audit signing is configured.</summary>
    public string? IntegritySignature { get; init; }
    /// <summary>Lowercase SHA-256 fingerprint of the signing key, never the key material.</summary>
    public string? IntegrityKeyFingerprint { get; init; }

    public static string ComputeIntegrityHash(AuditEvent value)
    {
        var data = value.Data
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var canonical = string.Join("\n", value.Id, value.InstanceId, value.Type,
            value.ActorId ?? string.Empty, value.CreatedAt.ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonSerializer.Serialize(data));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public bool HasValidIntegrity() =>
        IntegrityVersion == 1 && IntegrityHash is not null
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(IntegrityHash),
            Encoding.UTF8.GetBytes(ComputeIntegrityHash(this)));

    public bool HasAuthenticIntegrity(ReadOnlySpan<byte> signingKey) =>
        HasValidIntegrity()
        && IntegritySignature is not null
        && (IntegrityKeyFingerprint is null
            || string.Equals(IntegrityKeyFingerprint, ComputeKeyFingerprint(signingKey), StringComparison.Ordinal))
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(IntegritySignature),
            Encoding.UTF8.GetBytes(Convert.ToHexString(HMACSHA256.HashData(
                signingKey,
                Encoding.UTF8.GetBytes($"v{IntegrityVersion}:{IntegrityHash}"))).ToLowerInvariant()));

    public static string ComputeKeyFingerprint(ReadOnlySpan<byte> signingKey) =>
        Convert.ToHexString(SHA256.HashData(signingKey)).ToLowerInvariant();

    public bool HasAuthenticIntegrity(IEnumerable<byte[]> signingKeys)
    {
        ArgumentNullException.ThrowIfNull(signingKeys);
        var valid = false;
        foreach (var key in signingKeys)
            valid |= HasAuthenticIntegrity(key);
        return valid;
    }
}

/// <summary>A persisted signing key (ES256) for JWT issuance.</summary>
public sealed record SigningKeyRecord
{
    public required string Id { get; init; }               // skey_...
    public required string InstanceId { get; init; }
    public required string Algorithm { get; init; }        // ES256
    public required string SecretBase64 { get; init; }     // d||X||Y, 96 bytes
    public bool Active { get; init; }
    public long CreatedAt { get; init; }
    public long? RetiredAt { get; init; }
}

/// <summary>An upstream identity connection: social OIDC or enterprise SAML SSO.</summary>
public sealed record Connection
{
    public required string Id { get; init; }               // con_...
    public required string InstanceId { get; init; }
    public required string Type { get; init; }             // oidc | saml
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;
    public string? OrgId { get; init; }                    // scoped to an organization when set
    // OIDC fields.
    public string? IssuerUrl { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecretBox { get; init; }          // SecretBox(master key)
    public string? AuthorizationEndpoint { get; init; }
    public string? TokenEndpoint { get; init; }
    public string? UserinfoEndpoint { get; init; }
    // SAML fields.
    public string? IdpEntityId { get; init; }
    public string? SsoRedirectUrl { get; init; }
    public string? X509CertificatePem { get; init; }
    public string? SpEntityId { get; init; }               // defaults to the instance ACS URL
    public long CreatedAt { get; init; }
}

/// <summary>A SCIM directory: bearer-token-authenticated provisioning endpoint set.</summary>
public sealed record ScimDirectory
{
    public required string Id { get; init; }               // dir_...
    public required string InstanceId { get; init; }
    public string? OrgId { get; init; }                    // memberships land in this org
    public required string TokenKey { get; init; }         // sha256(bearer token) — unique
    public long CreatedAt { get; init; }
}

/// <summary>The instance's billing state, driven by Stripe webhooks.</summary>
public sealed record Subscription
{
    public required string Id { get; init; }               // sub_...
    public required string InstanceId { get; init; }
    public required string StripeCustomerId { get; init; }
    public string? StripeSubscriptionId { get; init; }
    public required string Plan { get; init; }             // free | pro | enterprise
    public required string Status { get; init; }           // active | trialing | past_due | canceled | incomplete
    public long? CurrentPeriodEnd { get; init; }
    public long CreatedAt { get; init; }
    public long UpdatedAt { get; init; }
}

/// <summary>A named JWT template: custom claims minted on demand per instance.</summary>
public sealed record JwtTemplate
{
    public required string Id { get; init; }               // jtp_...
    public required string InstanceId { get; init; }
    public required string Name { get; init; }             // url-safe, used in the mint route
    public required Dictionary<string, string> Claims { get; init; } // {{user.email}} substitution
    public int LifetimeSeconds { get; init; } = 300;
    public long CreatedAt { get; init; }
}

/// <summary>An organization enrollment rule by verified email domain.</summary>
public sealed record OrgDomain
{
    public required string Id { get; init; }               // dom_...
    public required string InstanceId { get; init; }
    public required string OrgId { get; init; }
    public required string Domain { get; init; }           // lowercase, e.g. acme.com
    public required string DomainKey { get; init; }        // instanceId:domain — unique
    public required long CreatedAt { get; init; }
}
