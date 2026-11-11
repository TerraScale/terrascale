using System.Net;
using System.Net.Sockets;

namespace TerraScale.DotNet;

internal sealed record TerraScaleService(string Host, bool Regional, string Name)
{
    internal static readonly TerraScaleService Api = new("api.terrascale.tech", true, "the TerraBase API");

    internal static readonly TerraScaleService Auth = new("auth.terrascale.tech", false, "ts-auth");

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

        return Regional && host.EndsWith("." + Host, StringComparison.Ordinal) && host[..^(Host.Length + 1)] is { Length: > 0 } label
            && !label.Contains('.', StringComparison.Ordinal);
    }
}

internal static class TerraScaleOrigin
{
    private static readonly IPNetwork NetBirdNetwork = IPNetwork.Parse("100.64.0.0/10");

    internal static Uri Parse(Uri? address, string label, string parameter, TerraScaleService service, bool netBird)
    {
        if (address is not { IsAbsoluteUri: true })
        {
            throw new ArgumentException($"{label} must be an absolute https URL.", parameter);
        }

        if (!(address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp && (address.IsLoopback || netBird && IsNetBird(address))))
        {
            throw new ArgumentException(netBird
                ? $"{label} must use https; plain http is allowed only for localhost, 127.0.0.0/8, [::1] and NetBird 100.64.0.0/10 addresses."
                : $"{label} must use https; plain http is allowed only for localhost, 127.0.0.0/8 and [::1].", parameter);
        }

        if (address.UserInfo.Length != 0)
        {
            throw new ArgumentException($"{label} must not carry user info.", parameter);
        }

        if (address.Query.Length != 0 || address.Fragment.Length != 0 || address.OriginalString.AsSpan().ContainsAny('?', '#'))
        {
            throw new ArgumentException($"{label} must not carry a query or a fragment.", parameter);
        }

        if (address.AbsolutePath != "/")
        {
            throw new ArgumentException($"{label} must be an origin without a path.", parameter);
        }

        if (!service.Serves(address.Host))
        {
            throw new ArgumentException($"{label} names a TerraScale host that does not serve {service.Name}.", parameter);
        }

        return new Uri(address.GetLeftPart(UriPartial.Authority) + "/");
    }

    private static bool IsNetBird(Uri address) =>
        IPAddress.TryParse(address.IdnHost, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && NetBirdNetwork.Contains(ip);
}
