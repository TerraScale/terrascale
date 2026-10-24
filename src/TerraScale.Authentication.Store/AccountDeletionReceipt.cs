namespace TerraScale.Authentication.Store;

/// <summary>A status-only capability digest, bound to one opaque subject and closure generation. Never stores the bearer.</summary>
public sealed record AccountDeletionReceipt(string InstanceId, string TokenHash, string SubjectId, string Generation, DateTimeOffset ExpiresAt, IReadOnlyList<string> OrganizationIds);
