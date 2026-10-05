using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary>
/// Document versions as entity tags and the conditional requests on them (RFC 9110 §8.8.3, §13.1.1, §13.1.2).
/// A present <c>If-Match</c> or <c>If-None-Match</c> must parse completely, or the request is a 400: it never
/// silently becomes unconditional. Only a bare <c>*</c> is the wildcard; a quoted <c>"*"</c> is an ordinary tag.
/// </summary>
internal static class ETags
{
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    /// <summary>The public version of a document state: <c>tbv1-</c> and 32 lowercase hex characters.</summary>
    public static string Version(Guid incarnation, string collection, string id, long version, ReadOnlyMemory<byte> json, bool deleted = false)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{incarnation:N}\0{collection}\0{id}\0{version}\0{(deleted ? 1 : 0)}\0")));
        hash.AppendData(json.Span);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        hash.GetHashAndReset(digest);
        return "tbv1-" + Convert.ToHexStringLower(digest[..16]);
    }

    /// <summary>Sends <paramref name="version"/> as the strong <c>ETag</c>.</summary>
    public static void Set(HttpContext context, string version) => context.Response.Headers.ETag = $"\"{version}\"";

    /// <summary>
    /// The preconditions of a write. <c>If-Match</c> compares strongly, so weak tags never match; <c>If-None-Match</c>
    /// on a write accepts only <c>*</c>.
    /// </summary>
    public static Precondition Precondition(HttpContext context)
    {
        var ifNoneMatch = Parse(context, HeaderNames.IfNoneMatch);
        if (ifNoneMatch is not null && !ifNoneMatch[0].Equals(EntityTagHeaderValue.Any))
        {
            throw new ApiException("writes accept only If-None-Match: *");
        }

        return FromIfMatch(Parse(context, HeaderNames.IfMatch)) with { IfNoneMatch = ifNoneMatch is not null };
    }

    /// <summary>
    /// Evaluates a read's conditions in RFC 9110 §13.2.2 order, before the read reports absence: a failed
    /// <c>If-Match</c> (strong) is a 412; then <see langword="true"/> when <c>If-None-Match</c> matches the live
    /// document, compared weakly, so the read answers 304.
    /// </summary>
    public static bool NotModified(HttpContext context, bool live, string? version)
    {
        var ifMatch = Parse(context, HeaderNames.IfMatch);
        var ifNoneMatch = Parse(context, HeaderNames.IfNoneMatch);
        if (ifMatch is not null)
        {
            Check(FromIfMatch(ifMatch), live, version);
        }

        return live && ifNoneMatch is not null && (ifNoneMatch[0].Equals(EntityTagHeaderValue.Any) || ifNoneMatch.Any(tag => Opaque(tag) == version));
    }

    /// <summary>
    /// A transaction operation's <c>if_match</c>: the contract's bare version (<c>"tbv1-…"</c>), or anything
    /// <c>If-Match</c> accepts, read by the same parser, so <c>"\"*\""</c> is a literal tag and never the wildcard.
    /// </summary>
    public static Precondition IfMatch(string value) =>
        FromIfMatch(IsVersion(value) ? [new EntityTagHeaderValue($"\"{value}\"")] : Parse(value, "if_match"));

    private static bool IsVersion(string value) =>
        value.Length == 37 && value.StartsWith("tbv1-", StringComparison.Ordinal) && !value.AsSpan(5).ContainsAnyExcept(LowerHex);

    /// <summary>
    /// Fails with <c>412 precondition_failed</c> unless <paramref name="precondition"/> holds for the current state:
    /// <paramref name="version"/> is the current version, a tombstone's included, or <see langword="null"/> when absent.
    /// </summary>
    public static void Check(Precondition precondition, bool live, string? version)
    {
        if (precondition.IfNoneMatch && live)
        {
            throw Failed("the document exists");
        }

        if ((precondition.IfMatchAny && !live) || (precondition.IfMatch is { } accepted && (version is null || !accepted.Contains(version))))
        {
            throw Failed("the document version does not match");
        }
    }

    // null when the header is absent; otherwise a non-empty list where * stands alone (RFC 9110: "*" / #entity-tag).
    private static IList<EntityTagHeaderValue>? Parse(HttpContext context, string header) =>
        context.Request.Headers[header] is { Count: > 0 } values ? Parse(values, header) : null;

    private static IList<EntityTagHeaderValue> Parse(StringValues values, string name) =>
        EntityTagHeaderValue.TryParseStrictList(values, out var tags)
        && tags.Count > 0
        && (tags.Count == 1 || !tags.Any(tag => tag.Equals(EntityTagHeaderValue.Any)))
            ? tags
            : throw new ApiException($"{name} must be * or a list of entity tags");

    // If-Match compares strongly, so weak tags never match.
    private static Precondition FromIfMatch(IList<EntityTagHeaderValue>? ifMatch)
    {
        var any = ifMatch?[0].Equals(EntityTagHeaderValue.Any) == true;
        return new Precondition(ifMatch is null || any ? null : [.. ifMatch.Where(tag => !tag.IsWeak).Select(Opaque)], any, IfNoneMatch: false);
    }

    // The tag between its quotes: the version a client copied from an ETag.
    private static string Opaque(EntityTagHeaderValue tag) => tag.Tag.Value![1..^1];

    private static ApiException Failed(string message) => new(StatusCodes.Status412PreconditionFailed, "precondition_failed", message);
}
