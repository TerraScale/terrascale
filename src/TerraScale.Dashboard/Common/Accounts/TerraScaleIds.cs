using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace TerraScale.Dashboard.Common.Accounts
{
    // Accept only canonical uppercase Crockford ULIDs, without prefixes or ambiguous letters.
    public static class TerraScaleIds
    {
        private static readonly SearchValues<char> Alphabet = SearchValues.Create("0123456789ABCDEFGHJKMNPQRSTVWXYZ");
        public static bool IsValid([NotNullWhen(true)] string? id)
        {
            if (id is not { Length: 26 } || id[0] is < '0' or > '7')
            {
                return false;
            }

            return !id.AsSpan().ContainsAnyExcept(Alphabet);
        }
        public static string Validate(string? id, [CallerArgumentExpression(nameof(id))] string? paramName = null)
        {
            return IsValid(id)
            ? id
            : throw new ArgumentException("Entity IDs must be plain 26-character uppercase Crockford ULIDs (first character 0–7), without prefixes or whitespace.", paramName);
        }
    }
}
