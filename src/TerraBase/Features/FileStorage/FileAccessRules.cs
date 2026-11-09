using TerraBase.Hosting;
using System.Text.Json;
using TerraBase.Errors;

namespace TerraBase.Features.FileStorage;

public sealed record FileAccessRules(string? Owner, FileAccessPolicy? Read, FileAccessPolicy? Write)
{
    public Validation? Check()
    {
        if (Owner is not null && !IsPointer(Owner))
        {
            return new Validation("owner must be a JSON Pointer of 1 to 16 object members", "invalid_rules");
        }

        if (Owner?.Split('/').Skip(1).FirstOrDefault()?.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal) == "id")
        {
            return new Validation("owner cannot address the virtual record id", "invalid_rules");
        }

        if (Read is null || Write is null || !Valid(Read) || !Valid(Write)
            || (Owner is null && (Read.Owner || Write.Owner)))
        {
            return new Validation("read and write require bounded roles; owner grants require an owner pointer", "invalid_rules");
        }

        return null;
    }

    private static bool IsPointer(string pointer) =>
        pointer.Length <= 4096 && pointer.StartsWith('/') && pointer[1..].Split('/') is { Length: <= 16 } segments
        && segments.All(segment => !segment.Replace("~0", "", StringComparison.Ordinal).Replace("~1", "", StringComparison.Ordinal).Contains('~', StringComparison.Ordinal));

    private static bool Valid(FileAccessPolicy policy) => policy.Roles is { Count: <= 32 }
        && policy.Roles.All(role => role is { Length: > 0 and <= 128 } && !role.Any(char.IsControl))
        && policy.Roles.Distinct(StringComparer.Ordinal).Count() == policy.Roles.Count;

    public string? Subject(ReadOnlyMemory<byte> json)
    {
        if (Owner is null || json.IsEmpty)
        {
            return null;
        }

        using var stored = JsonDocument.Parse(json, RequestJson.StoredJsonOptions);
        var value = stored.RootElement;
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
}

public sealed record FileAccessPolicy(bool Owner, bool Public, IReadOnlyList<string>? Roles);
