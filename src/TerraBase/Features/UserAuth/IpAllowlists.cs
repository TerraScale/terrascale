using System.Net;
using System.Net.Sockets;

namespace TerraBase.Features.UserAuth;

internal static class IpAllowlists
{
    public static readonly string[] Claims = ["key_cidrs", "project_cidrs"];

    private const int MaxEntries = 32;

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

    public static bool IsEdge(IPAddress? peer) => peer is not null && IPAddress.IsLoopback(Canonical(peer));

    public static IPAddress? ClientOf(HttpContext context)
    {
        var peer = context.Connection.RemoteIpAddress;
        if (IsEdge(peer) && context.Request.Headers["X-Forwarded-For"].ToString() is { Length: > 0 } forwarded)
        {
            var last = forwarded[(forwarded.LastIndexOf(',') + 1)..].Trim();
            return IPAddress.TryParse(last, out var client) ? Canonical(client) : null;
        }

        return peer is null ? null : Canonical(peer);
    }

    public static string Refusal(IPAddress? address) =>
        $"requests from {address?.ToString() ?? "an unknown address"} are not in this API key's or project's IP allowlist";

    private static IPAddress Canonical(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
