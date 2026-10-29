using System.Text.Json.Serialization;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace TerraScale.Dashboard.Platform.Http
{
    public sealed record CursorPage<T>(IReadOnlyList<T> Items, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor, int Limit)
    {
        public bool HasMore => NextCursor is not null;
    }

    // Cursors select a position, never authority. Every caller still reads its authorized source.
    public static class CursorPaging
    {
        public const string Description = "Cursor paging: limit 1–100 (default 100); pass nextCursor as after with the same scope and filters. Ordinal immutable-ID order. Continue until hasMore=false; nextCursor is then null. Invalid limits/cursors are rejected.";

        public static (int Limit, string After) Read(HttpRequest request)
        {
            var limit = 100;
            if (request.Query.TryGetValue("limit", out var text) &&
                (text.Count != 1 || !int.TryParse(text[0], NumberStyles.None, CultureInfo.InvariantCulture, out limit)))
            {
                throw Invalid();
            }

            var after = request.Query["after"];
            if (after.Count > 1)
            {
                throw Invalid();
            }

            ValidateLimit(limit);
            return (limit, after.ToString());
        }

        public static void ValidateLimit(int limit)
        {
            if (limit is < 1 or > 100)
            {
                throw Invalid();
            }
        }

        public static string Scope(AccountContext context, string resource, params string[] filters)
        {
            var parts = new[] { resource, context.UserId, context.Organization?.Id ?? "", context.Project?.Id ?? "" }.Concat(filters);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('.', parts.Select(Encode)))));
        }

        public static CursorPage<T> Page<T>(IEnumerable<T> source, Func<T, string> key, string scope, int limit = 100, string after = "")
        {
            ValidateLimit(limit);
            var position = "";
            if (after.Length > 0)
            {
                try
                {
                    var parts = after.Split('.');
                    if (after.Length > 2048 || parts.Length != 3 || parts[0] != "1" || parts[1] != scope)
                    {
                        throw Invalid();
                    }

                    position = new UTF8Encoding(false, true).GetString(WebEncoders.Base64UrlDecode(parts[2]));
                    if (position.Length is 0 or > 512 || Encode(position) != parts[2])
                    {
                        throw Invalid();
                    }
                }
                catch (Exception error) when (error is FormatException or DecoderFallbackException) { throw Invalid(); }
            }
            var ordered = source.Select(item => (Item: item, Key: key(item))).OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
            if (ordered.Any(item => string.IsNullOrEmpty(item.Key)) || ordered.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            {
                throw new IOException("The list producer returned invalid or duplicate identities.");
            }

            var page = ordered.Where(item => StringComparer.Ordinal.Compare(item.Key, position) > 0).Take(limit + 1).ToArray();
            return new([.. page.Take(limit).Select(item => item.Item)], page.Length > limit ? "1." + scope + "." + Encode(page[limit - 1].Key) : null, limit);
        }

        private static string Encode(string value)
        {
            return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(value));
        }

        private static AccountAccessException Invalid()
        {
            return new("Use limit 1–100 and an unchanged cursor from this list with the same scope and filters.", 422);
        }
    }
}
