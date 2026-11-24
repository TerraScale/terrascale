using System.Buffers;
using System.Globalization;
using System.Net;

namespace TerraScale.Contracts.Networking;

public sealed record InternalTerraBaseOrigin(Uri Alias, DnsEndPoint Target)
{
    private const string Loopback = "127.0.0.1";

    private static readonly SearchValues<char> OriginCharacters =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-.:[]/");

    private static readonly SearchValues<char> LabelCharacters =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789-");

    private static readonly SearchValues<char> HexCharacters =
        SearchValues.Create("0123456789abcdef");

    public static Uri ClientOrigin(Uri origin) => From(origin) is { } internalOrigin ? internalOrigin.Alias : origin;

    public static InternalTerraBaseOrigin? From(Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!origin.IsAbsoluteUri)
        {
            return null;
        }

        var raw = origin.OriginalString;
        if (raw.Length > 2048 || raw.AsSpan().ContainsAnyExcept(OriginCharacters))
        {
            return null;
        }

        var lower = raw.ToLowerInvariant();
        if (!lower.StartsWith("http://", StringComparison.Ordinal))
        {
            return null;
        }

        var rest = lower["http://".Length..];
        rest = rest.EndsWith('/') ? rest[..^1] : rest;
        if (rest.Length == 0 || rest.Contains('/', StringComparison.Ordinal))
        {
            return null;
        }

        var (host, after) = (rest, "");
        if (rest.StartsWith('['))
        {
            var end = rest.IndexOf(']', StringComparison.Ordinal);
            if (end < 0)
            {
                return null;
            }

            (host, after) = (rest[..(end + 1)], rest[(end + 1)..]);
            if (!IsPrivateNonLoopbackIpv6(host[1..^1]))
            {
                return null;
            }
        }
        else
        {
            if (rest.IndexOf(':', StringComparison.Ordinal) is var colon and >= 0)
            {
                (host, after) = (rest[..colon], rest[colon..]);
            }

            if (!IsInternalNonLoopbackNameOrIpv4(host))
            {
                return null;
            }
        }

        var port = 80;
        if (after.Length > 0)
        {
            var portText = after[1..];
            if (after[0] != ':' || !Digits(portText) || portText.Length > 5 || portText[0] == '0'
                || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                || port > 65535)
            {
                return null;
            }
        }

        if (origin.Port != port || !SameHost(origin, host))
        {
            return null;
        }

        var alias = port == 80
            ? new Uri($"http://{Loopback}/")
            : new Uri(string.Create(CultureInfo.InvariantCulture, $"http://{Loopback}:{port}/"));
        return new InternalTerraBaseOrigin(alias, new DnsEndPoint(origin.DnsSafeHost, origin.Port));
    }

    public DnsEndPoint Dial(DnsEndPoint requested) =>
        requested.Port == Target.Port && string.Equals(requested.Host, Loopback, StringComparison.Ordinal)
            ? Target
            : requested;

    private static bool Digits(string text) => text.Length > 0 && !text.AsSpan().ContainsAnyExceptInRange('0', '9');

    private static bool IsInternalNonLoopbackNameOrIpv4(string host)
    {
        if (host.Length == 0 || host.Length > 253)
        {
            return false;
        }

        var labels = host.Split('.');
        if (!labels.All(label => label.Length is >= 1 and <= 63
            && label[0] != '-'
            && label[^1] != '-'
            && !label.StartsWith("xn--", StringComparison.Ordinal)
            && !label.AsSpan().ContainsAnyExcept(LabelCharacters)))
        {
            return false;
        }

        var last = labels[^1];
        var numeric = Digits(last) || (last.StartsWith("0x", StringComparison.Ordinal) && !last.AsSpan(2).ContainsAnyExcept(HexCharacters));
        if (numeric)
        {
            if (labels.Length != 4)
            {
                return false;
            }

            Span<byte> octets = stackalloc byte[4];
            for (var i = 0; i < 4; i++)
            {
                var octet = labels[i];
                if (!Digits(octet) || octet.Length > 3 || (octet.Length > 1 && octet[0] == '0')
                    || !byte.TryParse(octet, NumberStyles.None, CultureInfo.InvariantCulture, out octets[i]))
                {
                    return false;
                }
            }

            return octets[0] == 10
                || (octets[0] == 172 && octets[1] is >= 16 and <= 31)
                || (octets[0] == 192 && octets[1] == 168)
                || (octets[0] == 100 && octets[1] is >= 64 and <= 127);
        }

        return host == "host.docker.internal" || (labels.Length >= 2 && last == "internal");
    }

    private static bool IsPrivateNonLoopbackIpv6(string text)
    {
        var halves = text.Split("::");
        if (text.Length == 0 || halves.Length > 2)
        {
            return false;
        }

        string[][] parts = [.. halves.Select(half => half.Length == 0 ? [] : half.Split(':'))];
        var count = parts.Sum(half => half.Length);
        if (parts.Length == 1 ? count != 8 : count > 7)
        {
            return false;
        }

        if (!parts.All(half => half.All(group => group.Length is >= 1 and <= 4 && !group.AsSpan().ContainsAnyExcept(HexCharacters))))
        {
            return false;
        }

        string[] groups = [.. parts[0], .. Enumerable.Repeat("0", 8 - count), .. parts.Length == 2 ? parts[1] : []];
        var first = ushort.Parse(groups[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return (first & 0xfe00) == 0xfc00;
    }

    private static bool SameHost(Uri origin, string host) =>
        host.StartsWith('[')
            ? IPAddress.TryParse(origin.DnsSafeHost, out var sent) && IPAddress.TryParse(host[1..^1], out var named) && sent.Equals(named)
            : string.Equals(origin.IdnHost, host, StringComparison.Ordinal);
}
