using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace TerraScale.Dashboard.Common.Live;

public sealed record LiveRefused(int Status);

public readonly union LiveAdmitted(LiveLease, LiveRefused);

public sealed class LiveLease(LiveAdmission owner, string client, string session) : IDisposable
{
    private int released;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref released, 1) == 0)
        {
            owner.Release(client, session);
        }
    }
}

public sealed class LiveAdmission
{
    internal const int PerSession = 12;
    internal const int PerClient = 48;
    internal const int Process = 4096;

    private readonly Lock gate = new();
    private readonly Dictionary<string, int> clients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> sessions = new(StringComparer.Ordinal);
    private int total;

    public LiveAdmitted Enter(HttpContext http)
    {
        var client = ClientKey(http.Connection.RemoteIpAddress);
        var session = DashboardCookies.Read(http, DashboardSessions.Cookie) is { Length: > 0 } token
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))
            : "anonymous:" + client;
        lock (gate)
        {
            if (total >= Process)
            {
                return new LiveRefused(StatusCodes.Status503ServiceUnavailable);
            }

            if (clients.GetValueOrDefault(client) >= PerClient || sessions.GetValueOrDefault(session) >= PerSession)
            {
                return new LiveRefused(StatusCodes.Status429TooManyRequests);
            }

            total++;
            clients[client] = clients.GetValueOrDefault(client) + 1;
            sessions[session] = sessions.GetValueOrDefault(session) + 1;
        }

        return new LiveLease(this, client, session);
    }

    private static string ClientKey(IPAddress? address)
    {
        if (address is null)
        {
            return "local";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out _))
        {
            return address.ToString();
        }

        bytes[8..].Clear();
        return new IPAddress(bytes).ToString() + "/64";
    }

    internal void Release(string client, string session)
    {
        lock (gate)
        {
            total--;
            Decrement(clients, client);
            Decrement(sessions, session);
        }
    }

    private static void Decrement(Dictionary<string, int> counts, string key)
    {
        if (counts.TryGetValue(key, out var count) && count > 1)
        {
            counts[key] = count - 1;
        }
        else
        {
            counts.Remove(key);
        }
    }
}
