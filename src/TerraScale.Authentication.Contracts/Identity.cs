using System.Text.Json.Serialization;

namespace TerraScale.Authentication.Contracts;

public sealed record UserRecord(string Id, string InstanceId, string Email, long AuthEpoch, DateTimeOffset CreatedAt, string? PasswordHash = null, string? PlatformRole = null)
{
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? ImageUrl { get; init; }
    public string? ExternalId { get; init; }
    public string PublicMetadata { get; init; } = "{}";
    public string PrivateMetadata { get; init; } = "{}";
    public string UnsafeMetadata { get; init; } = "{}";
    public string? CustomClaims { get; init; }
    public bool EmailVerified { get; init; } = true;
    public string? Username { get; init; }

    public string? AuthStamp { get; init; }

    public string? SessionStamp { get; init; }

    public bool Disabled { get; init; }

    public bool IsAnonymous { get; init; }

    public IReadOnlyList<LinkedProvider> LinkedProviders { get; init; } = [];

    [JsonIgnore]
    public IReadOnlyList<CredentialBinding> AuthenticationBindings { get; init; } = [];

    [JsonIgnore]
    public IReadOnlyList<CredentialBinding> StepUpBindings { get; init; } = [];

    [JsonIgnore]
    public bool MultiFactor { get; init; }

    [JsonIgnore]
    public string? MfaMethod { get; init; }

    [JsonIgnore]
    public bool InboxProven { get; init; }

    [JsonIgnore]
    public string? SessionId { get; init; }

    [JsonIgnore]
    public string? ActingAgentId { get; init; }

    [JsonIgnore]
    public IReadOnlyList<string> ActingAgentScopes { get; init; } = [];

    [JsonIgnore]
    public string? Version { get; init; }
}

public sealed record SessionRecord(string InstanceId, string UserId, string TokenHash, long AuthEpoch, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, bool Revoked = false)
{
    public string? Version { get; init; }

    public string Id { get; init; } = string.Empty;

    public DateTimeOffset? LastActivityAt { get; init; }

    public DateTimeOffset? VerifiedAt { get; init; }

    public string? SessionStamp { get; init; }

    public string? AuthStamp { get; init; }

    public IReadOnlyList<CredentialBinding> Bindings { get; init; } = [];

    public bool Mfa { get; init; }

    public string? MfaMethod { get; init; }
}

public sealed record PasscodeRecord(
    string InstanceId,
    string UserId,
    string Email,
    string CodeHash,
    long AuthEpoch,
    DateTimeOffset ExpiresAt,
    int FailedAttempts,
    bool Consumed,
    DateTimeOffset WindowStartedAt,
    int IssuedInWindow,
    string? Purpose = null,
    string? PasswordHash = null,
    string? PendingTokenHash = null)
{
    public string? Version { get; init; }

    public string? Username { get; init; }

    public string? AuthStamp { get; init; }

    public string IssueId { get; init; } = string.Empty;

    public IReadOnlyList<CredentialBinding> IssuanceBindings { get; init; } = [];

    public bool ResetsAuthentication { get; init; }

    public bool Pruned { get; init; }

    public string? StepUpRequestId { get; init; }

    public string Key { get; init; } = string.Empty;

    [JsonIgnore]
    public CredentialBinding? Binding { get; init; }

    [JsonIgnore]
    public UserRecord? AuthenticatedUser { get; init; }
}

public sealed record CredentialBinding(string Kind, string Key, string OwnerId);

public sealed record LinkedProvider(string Provider, string Issuer, string ClientId, string Subject);

public sealed record ProviderLinkRecord(string InstanceId, string UserId, string Provider, string Issuer, string ClientId, string Subject)
{
    public string? Version { get; init; }

    public LinkedProvider Identity => new(this.Provider, this.Issuer, this.ClientId, this.Subject);
}

public sealed record CredentialClaimRecord(string InstanceId, string Kind, string Key, string OwnerId)
{
    public string? Version { get; init; }
}

public sealed record ChallengeRecord(string InstanceId, string Purpose, string TokenHash, string SubjectId, DateTimeOffset ExpiresAt, bool Consumed = false)
{
    public string? Version { get; init; }
}
