using System.Text.Json;
using System.Security.Cryptography;
using TerraBase.Errors;
using TerraBase.Features.Documents;

namespace TerraBase.Engine;

/// <summary>A collection's declarative end-user permissions. Missing rules deny access.</summary>
/// <param name="Owner">JSON Pointer of the immutable owner subject, or null.</param>
/// <param name="Read">Read grants.</param>
/// <param name="Write">Write grants.</param>
public sealed record CollectionAccessRules(string? Owner, CollectionAccessPolicy? Read, CollectionAccessPolicy? Write)
{
    /// <summary>Rejects ambiguous or unbounded policies before they enter replicated metadata.</summary>
    public Validation? Check()
    {
        if (Owner is not null && QueryParser.CheckPointer(Owner, "invalid_rules", "owner must be a JSON Pointer of 1 to 16 object members") is Validation invalid)
        {
            return invalid;
        }

        if (Owner?.Split('/').Skip(1).FirstOrDefault()?.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal) == "id")
        {
            return new Validation("owner cannot address the virtual document id", "invalid_rules");
        }

        if (Read is null || Write is null || !Valid(Read) || !Valid(Write)
            || (Owner is null && (Read.Owner || Write.Owner)))
        {
            return new Validation("read and write require bounded roles; owner grants require an owner pointer", "invalid_rules");
        }

        return null;
    }

    /// <summary>Binds durable proofs and sync handles to the exact policy.</summary>
    public string Fingerprint() => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(this, CollectionRulesJsonContext.Default.CollectionAccessRules)));

    private static bool Valid(CollectionAccessPolicy policy) => policy.Roles is { Count: <= 32 }
        && policy.Roles.All(role => role is { Length: > 0 and <= 128 } && !role.Any(char.IsControl))
        && policy.Roles.Distinct(StringComparer.Ordinal).Count() == policy.Roles.Count;

    /// <summary>Reads an owner subject without coercing numbers, arrays or missing members.</summary>
    public string? Subject(ReadOnlyMemory<byte> json)
    {
        if (Owner is null || json.IsEmpty)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json, RequestJson.StoredDocumentOptions);
        var value = document.RootElement;
        foreach (var segment in Owner.Split('/').Skip(1))
        {
            var member = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(member, out value))
            {
                return null;
            }
        }

        return value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 256 } subject ? subject : null;
    }

    /// <summary>The owner predicate used before pagination, sorting and counting.</summary>
    public DocumentFilter OwnerFilter(string subject) => new CompareFilter(
        new DocumentPath([.. Owner!.Split('/').Skip(1).Select(segment => segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal))]),
        Comparison.Equal, new ScalarValue(ScalarKind.Text, subject));
}

/// <summary>Independent public, role and owner grants for one operation.</summary>
/// <param name="Owner">Whether the signed subject can access its own documents.</param>
/// <param name="Public">Whether every authenticated end-user can access documents.</param>
/// <param name="Roles">Trusted, case-sensitive roles with collection-wide access.</param>
public sealed record CollectionAccessPolicy(bool Owner, bool Public, IReadOnlyList<string>? Roles);

/// <summary>A bounded preimage proof retained with a feed event; never returned to clients.</summary>
/// <param name="Generation">Collection incarnation.</param>
/// <param name="Path">Owner pointer at the time of the write.</param>
/// <param name="Subject">Owner subject of the preimage or afterimage.</param>
/// <param name="Policy">Fingerprint of the complete declared policy.</param>
public sealed record DocumentOwnerProof(string Generation, string Path, string Subject, string Policy);
