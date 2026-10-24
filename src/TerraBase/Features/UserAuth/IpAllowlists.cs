using System.Net;
using System.Net.Sockets;

namespace TerraBase.Features.UserAuth;

/// <summary>
/// The IP allowlists ts-auth puts on an API key's tokens: <c>key_cidrs</c> (the key's own) and <c>project_cidrs</c> (its
/// project's), each space-separated canonical CIDRs. A client address must fall in every list the token carries.
/// </summary>
internal static class IpAllowlists
{
    /// <summary>The token claims that carry allowlists.</summary>
    public static readonly string[] Claims = ["key_cidrs", "project_cidrs"];

    private const int MaxEntries = 32;

    // Socket peers that only the platform edge (loopback, the host's container networks, the NetBird mesh) can be: their
    // forwarded client address is believed. The client listener is reachable from the internet only through the edge.
    private static readonly IPNetwork[] TrustedPeers =
    [
        IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("172.16.0.0/12"), IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("100.64.0.0/10"), IPNetwork.Parse("::1/128"), IPNetwork.Parse("fc00::/7"),
    ];

    /// <summary>One claim's networks; null when the claim is not a list of at most 32 CIDRs.</summary>
    public static IPNetwork[]? Parse(string claim)
    {
        var entries = claim.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length is 0 or > MaxEntries)
        {
            return null;
        }

        var networks = new IPNetwork[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            if (!IPNetwork.TryParse(entries[i], out networks[i]))
            {
                return null;
            }
        }

        return networks;
    }

    /// <summary>Whether <paramref name="address"/> falls in every one of <paramref name="allowlists"/>.</summary>
    public static bool Admits(IReadOnlyList<IPNetwork[]> allowlists, IPAddress? address)
    {
        if (allowlists.Count == 0)
        {
            return true;
        }

        if (address is null)
        {
            return false;
        }

        address = Canonical(address);
        return allowlists.All(list => list.Any(network => network.Contains(address)));
    }

    /// <summary>Whether a connection from <paramref name="peer"/> may name the real client (forwarded header, PROXY protocol).</summary>
    public static bool IsTrustedPeer(IPAddress? peer) => peer is not null && TrustedPeers.Any(network => network.Contains(Canonical(peer)));

    /// <summary>The client's address: the edge's rightmost <c>X-Forwarded-For</c> hop from a trusted peer, else the socket peer.</summary>
    public static IPAddress? ClientOf(HttpContext context)
    {
        var peer = context.Connection.RemoteIpAddress;
        if (IsTrustedPeer(peer) && context.Request.Headers["X-Forwarded-For"].ToString() is { Length: > 0 } forwarded)
        {
            var last = forwarded[(forwarded.LastIndexOf(',') + 1)..].Trim();
            return IPAddress.TryParse(last, out var client) ? Canonical(client) : null;
        }

        return peer is null ? null : Canonical(peer);
    }

    /// <summary>The 403 detail: the address the request came from, so its owner can add it.</summary>
    public static string Refusal(IPAddress? address) =>
        $"requests from {address?.ToString() ?? "an unknown address"} are not in this API key's or project's IP allowlist";

    private static IPAddress Canonical(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
