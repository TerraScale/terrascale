using System.Buffers;
using System.Security.Cryptography;
using Cysharp.Text;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using TerraBase.Errors;

namespace TerraBase.Features.Documents;

/// <summary>Whether a read answers 304, or why its conditions refuse it.</summary>
internal readonly union NotModifiedResult(bool, Validation, PreconditionFailed);

/// <summary>A precondition that holds, or the 412 it fails with.</summary>
internal readonly union VersionCheck(Done, PreconditionFailed);

/// <summary>
/// Document versions as entity tags and the conditional requests on them (RFC 9110 §8.8.3, §13.1.1, §13.1.2).
/// A present <c>If-Match</c> or <c>If-None-Match</c> must parse completely, or the request is a 400: it never
/// silently becomes unconditional. Only a bare <c>*</c> is the wildcard; a quoted <c>"*"</c> is an ordinary tag.
/// </summary>
internal static class ETags
{
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    /// <summary>
    /// The public version of a document state: <c>tbv1-</c> and 32 lowercase hex characters. It hashes the replicated state
    /// under the database's <see cref="Engine.IStorageTransaction.Scope"/>, so every member serving the database answers the same one.
    /// </summary>
    public static string Version(Guid scope, string collection, string id, long version, ReadOnlyMemory<byte> json, bool deleted = false)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var input = ZString.CreateUtf8StringBuilder();
        input.AppendFormat("{0:N}\0{1}\0{2}\0{3}\0{4}\0", scope, collection, id, version, deleted ? 1 : 0);
        hash.AppendData(input.AsSpan());
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
    public static Valid<Precondition> CheckPrecondition(HttpContext context) =>
        (Tags(context, HeaderNames.IfNoneMatch), Tags(context, HeaderNames.IfMatch)) switch
        {
            (Validation error, _) => error,
            (IList<EntityTagHeaderValue> ifNoneMatch, _) when ifNoneMatch.Count > 0 && !ifNoneMatch[0].Equals(EntityTagHeaderValue.Any) =>
                new Validation("writes accept only If-None-Match: *"),
            (_, Validation error) => error,
            (IList<EntityTagHeaderValue> ifNoneMatch, IList<EntityTagHeaderValue> ifMatch) => FromIfMatch(ifMatch) with { IfNoneMatch = ifNoneMatch.Count > 0 },
        };

    /// <summary>
    /// Evaluates a read's conditions in RFC 9110 §13.2.2 order, before the read reports absence: a failed
    /// <c>If-Match</c> (strong) is a 412; then <see langword="true"/> when <c>If-None-Match</c> matches the live
    /// document, compared weakly, so the read answers 304.
    /// </summary>
    public static NotModifiedResult CheckNotModified(HttpContext context, bool live, string? version) =>
        (Tags(context, HeaderNames.IfMatch), Tags(context, HeaderNames.IfNoneMatch)) switch
        {
            (Validation error, _) => error,
            (_, Validation error) => error,
            (IList<EntityTagHeaderValue> ifMatch, _) when ifMatch.Count > 0 && CheckVersion(FromIfMatch(ifMatch), live, version) is PreconditionFailed failed => failed,
            (_, IList<EntityTagHeaderValue> ifNoneMatch) =>
                live && ifNoneMatch.Count > 0 && (ifNoneMatch[0].Equals(EntityTagHeaderValue.Any) || ifNoneMatch.Any(tag => Opaque(tag) == version)),
        };

    /// <summary>
    /// A transaction operation's <c>if_match</c>: the contract's bare version (<c>"tbv1-…"</c>), or anything
    /// <c>If-Match</c> accepts, read by the same parser, so <c>"\"*\""</c> is a literal tag and never the wildcard.
    /// </summary>
    public static Valid<Precondition> CheckIfMatch(string value) =>
        IsVersion(value)
            ? FromIfMatch([new EntityTagHeaderValue($"\"{value}\"")])
            : Tags(value, "if_match") switch
            {
                IList<EntityTagHeaderValue> tags => FromIfMatch(tags),
                Validation error => error,
            };

    private static bool IsVersion(string value) =>
        value.Length == 37 && value.StartsWith("tbv1-", StringComparison.Ordinal) && !value.AsSpan(5).ContainsAnyExcept(LowerHex);

    /// <summary>
    /// Fails with <c>412 precondition_failed</c> unless <paramref name="precondition"/> holds for the current state:
    /// <paramref name="version"/> is the current version, a tombstone's included, or <see langword="null"/> when absent.
    /// </summary>
    public static VersionCheck CheckVersion(Precondition precondition, bool live, string? version) =>
        precondition.IfNoneMatch && live ? Failed("the document exists")
        : (precondition.IfMatchAny && !live) || (precondition.IfMatch is { } accepted && (version is null || !accepted.Contains(version)))
            ? Failed("the document version does not match")
            : new Done();

    // Empty when the header is absent; otherwise a non-empty list where * stands alone (RFC 9110: "*" / #entity-tag).
    private static Valid<IList<EntityTagHeaderValue>> Tags(HttpContext context, string header)
    {
        if (context.Request.Headers[header] is { Count: > 0 } values)
        {
            return Tags(values, header);
        }

        IList<EntityTagHeaderValue> absent = [];
        return absent;
    }

    private static Valid<IList<EntityTagHeaderValue>> Tags(StringValues values, string name)
    {
        if (EntityTagHeaderValue.TryParseStrictList(values, out var tags)
            && tags.Count > 0
            && (tags.Count == 1 || !tags.Any(tag => tag.Equals(EntityTagHeaderValue.Any))))
        {
            return tags;
        }

        return new Validation($"{name} must be * or a list of entity tags");
    }

    // If-Match compares strongly, so weak tags never match; an empty list means no If-Match.
    private static Precondition FromIfMatch(IList<EntityTagHeaderValue> ifMatch)
    {
        var any = ifMatch.Count > 0 && ifMatch[0].Equals(EntityTagHeaderValue.Any);
        return new Precondition(ifMatch.Count == 0 || any ? null : [.. ifMatch.Where(tag => !tag.IsWeak).Select(Opaque)], any, IfNoneMatch: false);
    }

    // The tag between its quotes: the version a client copied from an ETag.
    private static string Opaque(EntityTagHeaderValue tag) => tag.Tag.Value![1..^1];

    private static PreconditionFailed Failed(string message) => new(message, "precondition_failed");
}
