using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace TerraScale.DotNet;

/// <summary>Validates plain, canonical uppercase TerraScale entity ULIDs.</summary>
public static class TerraScaleIds
{
    private static readonly SearchValues<char> Alphabet = SearchValues.Create("0123456789ABCDEFGHJKMNPQRSTVWXYZ");

    /// <summary>
    /// Returns whether <paramref name="id"/> is exactly 26 uppercase Crockford Base32 characters encoding
    /// a 128-bit ULID. Prefixes, UUIDs, whitespace, lowercase and the ambiguous letters I, L, O and U are rejected.
    /// </summary>
    public static bool IsValid([NotNullWhen(true)] string? id)
    {
        if (id is not { Length: 26 } || id[0] is < '0' or > '7')
        {
            return false;
        }

        return !id.AsSpan().ContainsAnyExcept(Alphabet);
    }

    /// <summary>Returns the unchanged canonical ULID, or throws an actionable argument error.</summary>
    /// <param name="id">The entity ID to validate.</param>
    /// <param name="paramName">The argument name, supplied automatically for direct calls.</param>
    /// <exception cref="ArgumentException">The ID is not a plain canonical uppercase ULID.</exception>
    public static string Validate(string? id, [CallerArgumentExpression(nameof(id))] string? paramName = null) =>
        IsValid(id)
            ? id
            : throw new ArgumentException("Entity IDs must be plain 26-character uppercase Crockford ULIDs (first character 0–7), without prefixes or whitespace.", paramName);
}
