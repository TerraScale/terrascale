using System.Buffers;
using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Unicode;

namespace TerraScale.DotNet.TerraBase;

internal sealed record MalformedBookmark;

internal sealed record OversizedBookmark;

internal readonly union BookmarkMerge(string, MalformedBookmark, OversizedBookmark);

internal static class TerraBaseBookmark
{
    public const int MaxEncodedLength = 1024;

    private const int MaxRegionBytes = 64;
    private const byte FormatVersion = 1;

    private static readonly MalformedBookmark Malformed = new();
    private static readonly OversizedBookmark Oversized = new();

    public static BookmarkMerge Merge(string current, string? observed)
    {
        if (string.IsNullOrEmpty(observed))
        {
            return current;
        }

        if (!TryDecode(current, out var mine) || !TryDecode(observed, out var theirs))
        {
            return Malformed;
        }

        Dictionary<string, ulong> newest = new(StringComparer.Ordinal);
        foreach (var (region, sequence) in mine.Concat(theirs))
        {
            newest[region] = newest.TryGetValue(region, out var known) ? Math.Max(known, sequence) : sequence;
        }

        var encoded = Encode([.. newest.OrderBy(static pair => pair.Key, StringComparer.Ordinal)]);
        return encoded.Length > MaxEncodedLength ? Oversized : encoded;
    }

    private static bool TryDecode(string encoded, out IReadOnlyList<KeyValuePair<string, ulong>> entries)
    {
        entries = [];
        if (encoded.Length == 0)
        {
            return true;
        }

        if (encoded.Length > MaxEncodedLength)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[Base64Url.GetMaxDecodedLength(MaxEncodedLength)];
        if (Base64Url.DecodeFromChars(encoded, buffer, out _, out var written) != OperationStatus.Done || written == 0 || buffer[0] != FormatVersion)
        {
            return false;
        }

        ReadOnlySpan<byte> cursor = buffer[1..written];
        if (!TryReadVarUInt(ref cursor, out var count) || count > (ulong)cursor.Length)
        {
            return false;
        }

        var decoded = new KeyValuePair<string, ulong>[(int)count];
        for (var index = 0; index < decoded.Length; index++)
        {
            if (!TryReadVarUInt(ref cursor, out var length) || length is 0 or > MaxRegionBytes || length > (ulong)cursor.Length || !Utf8.IsValid(cursor[..(int)length]))
            {
                return false;
            }

            var region = Encoding.UTF8.GetString(cursor[..(int)length]);
            cursor = cursor[(int)length..];
            if (!TryReadVarUInt(ref cursor, out var sequence) || sequence is 0 or > long.MaxValue
                || (index > 0 && string.CompareOrdinal(decoded[index - 1].Key, region) >= 0))
            {
                return false;
            }

            decoded[index] = new(region, sequence);
        }

        if (!cursor.IsEmpty)
        {
            return false;
        }

        entries = decoded;
        return true;
    }

    private static string Encode(IReadOnlyList<KeyValuePair<string, ulong>> entries)
    {
        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var writer = new ArrayBufferWriter<byte>(8 + (entries.Count * 16));
        writer.GetSpan(1)[0] = FormatVersion;
        writer.Advance(1);
        WriteVarUInt(writer, (ulong)entries.Count);
        foreach (var (region, sequence) in entries)
        {
            var length = Encoding.UTF8.GetByteCount(region);
            WriteVarUInt(writer, (ulong)length);
            writer.Advance(Encoding.UTF8.GetBytes(region, writer.GetSpan(length)));
            WriteVarUInt(writer, sequence);
        }

        return Base64Url.EncodeToString(writer.WrittenSpan);
    }

    private static void WriteVarUInt(ArrayBufferWriter<byte> writer, ulong value)
    {
        var span = writer.GetSpan(10);
        var written = 0;
        while (value >= 0x80)
        {
            span[written++] = (byte)(value | 0x80);
            value >>= 7;
        }

        span[written++] = (byte)value;
        writer.Advance(written);
    }

    private static bool TryReadVarUInt(ref ReadOnlySpan<byte> source, out ulong value)
    {
        value = 0;
        for (var index = 0; index < 10 && index < source.Length; index++)
        {
            var current = source[index];
            if (index == 9 && current > 1)
            {
                return false;
            }

            value |= (ulong)(current & 0x7F) << (7 * index);
            if (current < 0x80)
            {
                source = source[(index + 1)..];
                return true;
            }
        }

        value = 0;
        return false;
    }
}

internal sealed class TerraBaseBookmarkTracker
{
    private readonly Lock gate = new();
    private string current = string.Empty;

    public string Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    public void Observe(string bookmark, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(bookmark, parameterName);
        lock (gate)
        {
            current = Requested(TerraBaseBookmark.Merge(current, bookmark), parameterName);
        }
    }

    public string Outgoing(string? requested) =>
        Requested(TerraBaseBookmark.Merge(Current, requested), nameof(TerraBaseSqlRequestOptions.Bookmark));

    public void ObserveAnswer(string? bookmark, HttpStatusCode? status)
    {
        lock (gate)
        {
            var merged = TerraBaseBookmark.Merge(current, bookmark);
            current = merged switch
            {
                string next => next,
                MalformedBookmark => throw Invalid(status),
                OversizedBookmark => bookmark ?? throw Invalid(status),
            };
        }
    }

    private static string Requested(BookmarkMerge merged, string parameterName) => merged switch
    {
        string next => next,
        MalformedBookmark => throw new ArgumentException("The bookmark must be one TerraScale-Bookmark value returned by TerraScale.", parameterName),
        OversizedBookmark => throw new ArgumentException(
            $"The bookmark merged with the tracked bookmark exceeds {TerraBaseBookmark.MaxEncodedLength} characters; start from a fresh client to drop regions.", parameterName),
    };

    public void ObserveResponse(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues(TerraBaseConsistency.BookmarkHeader, out var values))
        {
            ObserveAnswer(values.ToArray() is [var value] ? value : throw Invalid(response.StatusCode), response.StatusCode);
        }
    }

    private static TerraBaseException Invalid(HttpStatusCode? status) =>
        new(status, "TerraBase returned an invalid TerraScale-Bookmark.", "invalid_response", null);
}
