namespace TerraScale.Auth.Store;

/// <summary>A ts-auth customer instance (one tenant of the IdP).</summary>
public sealed record Instance
{
    public required string Id { get; init; }               // ins_...
    public required string Slug { get; init; }             // unique, url-safe
    public required string Name { get; init; }
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
    public long CreatedAt { get; init; }
}

/// <summary>An outbound webhook endpoint for instance events.</summary>
public sealed record WebhookEndpoint
{
    public required string Id { get; init; }               // wh_...
    public required string InstanceId { get; init; }
    public required string Url { get; init; }
    public required string[] Events { get; init; }
    public required string SigningSecret { get; init; }    // stored plaintext: endpoints sign outbound payloads
    public bool Enabled { get; init; } = true;
    public long CreatedAt { get; init; }
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
