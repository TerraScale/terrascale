namespace TerraScale.Platform.Features.FileStorage;

public sealed record FileAccessRules(string? Owner, FileAccessPolicy? Read, FileAccessPolicy? Write)
{
    public PlatformInvalidRequest? Check()
    {
        if (Owner is not null && !IsPointer(Owner))
        {
            return new PlatformInvalidRequest("owner must be a JSON Pointer of 1 to 16 object members", "invalid_rules");
        }

        if (Owner is not null && Segments(Owner).FirstOrDefault() == "id")
        {
            return new PlatformInvalidRequest("owner cannot address the virtual record id", "invalid_rules");
        }

        if (Read is null || Write is null || !Valid(Read) || !Valid(Write) || (Owner is null && (Read.Owner || Write.Owner)))
        {
            return new PlatformInvalidRequest("read and write require bounded roles; owner grants require an owner pointer", "invalid_rules");
        }

        return null;
    }

    public string? Subject(JsonElement metadata)
    {
        if (Owner is null)
        {
            return null;
        }

        var value = metadata;
        foreach (var member in Segments(Owner))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(member, out value))
            {
                return null;
            }
        }

        return value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 256 } subject ? subject : null;
    }

    public bool Allows(AccessContext access, JsonElement metadata, bool write)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (access.Grant.DatabaseRestricted)
        {
            return false;
        }

        if (!access.Grant.EndUser)
        {
            return true;
        }

        var policy = write ? Write : Read;
        return BucketWide(policy, access) || (policy?.Owner == true && Subject(metadata) is { } subject && subject == access.Grant.Subject);
    }

    public bool Grants(AccessContext access, bool write)
    {
        ArgumentNullException.ThrowIfNull(access);
        return (write ? Write : Read) is { } policy && (BucketWide(policy, access) || (policy.Owner && access.Grant.Subject.Length > 0));
    }

    public bool CanCreate(AccessContext access, JsonElement metadata)
    {
        ArgumentNullException.ThrowIfNull(access);
        return Allows(access, metadata, write: true)
            && (!access.Grant.EndUser || Owner is null || Subject(metadata) == access.Grant.Subject);
    }

    private static bool BucketWide(FileAccessPolicy? policy, AccessContext access) =>
        policy is not null
        && ((policy.Public && !access.Guest) || (policy.Roles is { } roles && roles.Any(role => access.Grant.Roles.Contains(role, StringComparer.Ordinal))));

    private static IEnumerable<string> Segments(string pointer) =>
        pointer.Split('/').Skip(1).Select(static segment => segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));

    private static bool IsPointer(string pointer) =>
        pointer.Length <= 4096 && pointer.StartsWith('/') && pointer[1..].Split('/') is { Length: <= 16 } segments
        && segments.All(static segment => !segment.Replace("~0", "", StringComparison.Ordinal).Replace("~1", "", StringComparison.Ordinal).Contains('~', StringComparison.Ordinal));

    private static bool Valid(FileAccessPolicy policy) =>
        policy.Roles is { Count: <= 32 } roles
        && roles.All(static role => role is { Length: > 0 and <= 128 } && !role.Any(char.IsControl))
        && roles.Distinct(StringComparer.Ordinal).Count() == roles.Count;
}
