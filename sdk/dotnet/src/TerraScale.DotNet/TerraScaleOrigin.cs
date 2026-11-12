using System.Buffers;
using System.Globalization;
using System.Net;

namespace TerraScale.DotNet;

internal sealed record TerraScaleService(string Host, string Name)
{
    internal static readonly TerraScaleService Api = new("api.terrascale.tech", "the TerraBase API");

    internal static readonly TerraScaleService Auth = new("auth.terrascale.tech", "ts-auth");

    internal bool Serves(string host)
    {
        if (host != "terrascale.tech" && !host.EndsWith(".terrascale.tech", StringComparison.Ordinal))
        {
            return true;
        }

        if (host == Host)
        {
            return true;
        }

        return host.EndsWith("." + Host, StringComparison.Ordinal) && host[..^(Host.Length + 1)] is { Length: > 0 } label
            && !label.Contains('.', StringComparison.Ordinal);
    }
}

internal static class TerraScaleOrigin
{
    private static readonly SearchValues<char> OriginCharacters =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-.:[]/");

    private static readonly SearchValues<char> LabelCharacters = SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789-");

    private static readonly SearchValues<char> HexCharacters = SearchValues.Create("0123456789abcdef");

    internal static Uri Parse(Uri? address, string label, string parameter, TerraScaleService service)
    {
        var (scheme, host, port) = Strict(address?.OriginalString) ?? throw Malformed(label, parameter, service);
        if (scheme == Uri.UriSchemeHttp && !Loopback(host))
        {
            throw new ArgumentException($"{label} must use https; plain http is allowed only for localhost, 127.0.0.0/8 and [::1].", parameter);
        }

        if (!service.Serves(host))
        {
            throw new ArgumentException($"{label} names a TerraScale host that does not serve {service.Name}.", parameter);
        }

        if (!Uri.TryCreate($"{scheme}://{host}{(port.Length == 0 ? "" : ":" + port)}/", UriKind.Absolute, out var origin) || !SameHost(origin, host, port))
        {
            throw new ArgumentException($"{label} is read as another host or port by the HTTP client.", parameter);
        }

        return origin;
    }

    internal static Uri Address(string raw, string label, TerraScaleService service) =>
        Uri.TryCreate(raw, UriKind.Absolute, out var address) ? address : throw Malformed(label, null, service);

    internal static HttpResponseMessage Sent(HttpResponseMessage response, Uri origin)
    {
        if (response.RequestMessage?.RequestUri is not { } sent
            || Uri.Compare(sent, origin, UriComponents.SchemeAndServer, UriFormat.UriEscaped, StringComparison.Ordinal) == 0)
        {
            return response;
        }

        response.Dispose();
        throw new InvalidOperationException(
            $"The HttpClient followed a redirect away from {origin.GetLeftPart(UriPartial.Authority)}: disable automatic redirects on its handler, as TerraBaseHttp.CreateHandler() does.");
    }

    private static ArgumentException Malformed(string label, string? parameter, TerraScaleService service) =>
        new($"{label} must be exactly scheme://host[:port], such as https://{service.Host}.", parameter);

    private static (string Scheme, string Host, string Port)? Strict(string? raw)
    {
        if (raw is null || raw.Length > 2048 || raw.AsSpan().ContainsAnyExcept(OriginCharacters))
        {
            return null;
        }

        var lower = raw.ToLowerInvariant();
        var split = lower.IndexOf("://", StringComparison.Ordinal);
        if (split < 0)
        {
            return null;
        }

        var scheme = lower[..split];
        var rest = lower[(split + 3)..];
        rest = rest.EndsWith('/') ? rest[..^1] : rest;
        if (scheme is not ("https" or "http") || rest.Contains('/', StringComparison.Ordinal))
        {
            return null;
        }

        var (host, after) = (rest, "");
        if (rest.StartsWith('['))
        {
            var end = rest.IndexOf(']', StringComparison.Ordinal);
            if (end < 0 || Ipv6(rest[1..end]) is not { } groups || groups.All(group => group == 0))
            {
                return null;
            }

            (host, after) = (rest[..(end + 1)], rest[(end + 1)..]);
        }
        else if (rest.IndexOf(':', StringComparison.Ordinal) is var colon and >= 0)
        {
            (host, after) = (rest[..colon], rest[colon..]);
        }

        if (!host.StartsWith('[') && !Name(host))
        {
            return null;
        }

        if (after.Length == 0)
        {
            return (scheme, host, "");
        }

        var port = after[1..];
        if (after[0] != ':' || !Digits(port) || port.Length > 5 || port[0] == '0' || int.Parse(port, CultureInfo.InvariantCulture) > 65535)
        {
            return null;
        }

        return (scheme, host, scheme == "https" && port == "443" || scheme == "http" && port == "80" ? "" : port);
    }

    private static bool Digits(string text) => text.Length > 0 && !text.AsSpan().ContainsAnyExceptInRange('0', '9');

    private static bool Ipv4(string host) =>
        host.Split('.') is { Length: 4 } octets
        && octets.All(octet => Digits(octet) && octet.Length <= 3 && !(octet.Length > 1 && octet[0] == '0') && int.Parse(octet, CultureInfo.InvariantCulture) <= 255);

    private static ushort[]? Ipv6(string text)
    {
        var halves = text.Split("::");
        if (text.Length == 0 || halves.Length > 2)
        {
            return null;
        }

        string[][] parts = [.. halves.Select(half => half.Length == 0 ? [] : half.Split(':'))];
        var count = parts.Sum(half => half.Length);
        if (parts.Length == 1 ? count != 8 : count > 7)
        {
            return null;
        }

        if (!parts.All(half => half.All(group => group.Length is >= 1 and <= 4 && !group.AsSpan().ContainsAnyExcept(HexCharacters))))
        {
            return null;
        }

        string[] groups = [.. parts[0], .. Enumerable.Repeat("0", 8 - count), .. parts.Length == 2 ? parts[1] : []];
        return [.. groups.Select(group => ushort.Parse(group, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture))];
    }

    private static bool Name(string host)
    {
        if (host.Length > 253 || host is "loopback" or "0.0.0.0")
        {
            return false;
        }

        var labels = host.Split('.');
        if (!labels.All(label => label.Length is >= 1 and <= 63 && label[0] != '-' && label[^1] != '-' && !label.StartsWith("xn--", StringComparison.Ordinal)
            && !label.AsSpan().ContainsAnyExcept(LabelCharacters)))
        {
            return false;
        }

        var last = labels[^1];
        var numeric = Digits(last) || last.StartsWith("0x", StringComparison.Ordinal) && !last.AsSpan(2).ContainsAnyExcept(HexCharacters);
        return !numeric || Ipv4(host);
    }

    private static bool Loopback(string host) =>
        host == "localhost" || host.StartsWith("127.", StringComparison.Ordinal) && Ipv4(host)
        || host.StartsWith('[') && Ipv6(host[1..^1]) is [0, 0, 0, 0, 0, 0, 0, 1];

    private static bool SameHost(Uri origin, string host, string port) =>
        (origin.IsDefaultPort ? "" : origin.Port.ToString(CultureInfo.InvariantCulture)) == port
        && (host.StartsWith('[')
            ? IPAddress.TryParse(origin.IdnHost, out var sent) && IPAddress.TryParse(host[1..^1], out var named) && sent.Equals(named)
            : origin.IdnHost == host);
}
