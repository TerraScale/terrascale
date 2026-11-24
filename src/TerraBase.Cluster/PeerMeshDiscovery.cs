using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TerraBase.Cluster;

public readonly union PeerDiscoveryOutcome(IReadOnlyList<PeerNodeInfo>, ClusterConfigurationError);

public static class PeerMeshDiscovery
{
    public const string OrchestratorPeersPath = "/run/ts-orch/peers.json";
    public const string PeersFileVariable = "TS_PEERS_FILE";

    private static readonly char[] Separators = [' ', '\t'];

    private static readonly SearchValues<char> SegmentCharacters =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.");

    public static PeerDiscoveryOutcome Discover(string? peersFile, int defaultQuicPort, int defaultInternalHttpPort)
    {
        if (File.Exists(OrchestratorPeersPath))
        {
            return Load(OrchestratorPeersPath, defaultQuicPort, defaultInternalHttpPort);
        }

        return string.IsNullOrWhiteSpace(peersFile) ? Array.Empty<PeerNodeInfo>() : Load(peersFile, defaultQuicPort, defaultInternalHttpPort);
    }

    public static PeerDiscoveryOutcome Load(string path, int defaultQuicPort, int defaultInternalHttpPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ClusterFiles.ReadBytes(path) switch
        {
            byte[] content => Parse(content, defaultQuicPort, defaultInternalHttpPort),
            ClusterConfigurationError invalid => invalid,
        };
    }

    public static PeerDiscoveryOutcome Parse(ReadOnlySpan<byte> content, int defaultQuicPort, int defaultInternalHttpPort)
    {
        var trimmed = content.Trim(" \t\r\n"u8);
        if (trimmed.IsEmpty)
        {
            return Array.Empty<PeerNodeInfo>();
        }

        return trimmed[0] is (byte)'[' or (byte)'{'
            ? ParseJson(trimmed, defaultQuicPort, defaultInternalHttpPort)
            : ParseLines(Encoding.UTF8.GetString(trimmed), defaultQuicPort, defaultInternalHttpPort);
    }

    public static PeerDiscoveryOutcome ParseLines(string text, int defaultQuicPort, int defaultInternalHttpPort)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<PeerNodeInfo> peers = [];
        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var fields = line.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length is < 3 or > 5)
            {
                return Invalid(string.Concat("peers line ", lineNumber.ToString(CultureInfo.InvariantCulture), " must be '<nodeId> <region> <host> [quicPort] [internalHttpPort]'"));
            }

            var quicPort = fields.Length > 3 ? ParsePort(fields[3]) : defaultQuicPort;
            var internalPort = fields.Length > 4 ? ParsePort(fields[4]) : defaultInternalHttpPort;
            if (Peer(fields[0], fields[1], fields[2], quicPort, internalPort) is not { } peer)
            {
                return Invalid(string.Concat("peers line ", lineNumber.ToString(CultureInfo.InvariantCulture), " is not a valid peer"));
            }

            peers.Add(peer);
        }

        return Distinct(peers);
    }

    public static PeerDiscoveryOutcome ParseJson(ReadOnlySpan<byte> json, int defaultQuicPort, int defaultInternalHttpPort)
    {
        var parsed = OpenJson(json);
        if (parsed is null)
        {
            return Invalid("peers JSON is malformed");
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            var array = root.ValueKind switch
            {
                JsonValueKind.Array => root,
                JsonValueKind.Object => PeersProperty(root),
                JsonValueKind.Undefined or JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => default,
                _ => throw new UnreachableException(),
            };
            if (array.ValueKind != JsonValueKind.Array)
            {
                return Invalid("peers JSON must be an array or an object with a 'peers' array");
            }

            List<PeerNodeInfo> peers = [];
            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    return Invalid("every peers JSON entry must be an object");
                }

                var quicPort = Integer(element, "quicPort") ?? defaultQuicPort;
                var internalPort = Integer(element, "internalHttpPort") ?? defaultInternalHttpPort;
                if (Peer(Text(element, "nodeId"), Text(element, "region"), Text(element, "host"), quicPort, internalPort) is not { } peer)
                {
                    return Invalid("peers JSON entry is missing nodeId, region or host, or carries an invalid port");
                }

                peers.Add(peer);
            }

            return Distinct(peers);
        }
    }

    private static JsonDocument? OpenJson(ReadOnlySpan<byte> json)
    {
        try
        {
            return JsonDocument.Parse(json.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement PeersProperty(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.NameEquals("peers") || property.NameEquals("meshPeers"))
            {
                return property.Value;
            }
        }

        return default;
    }

    private static string? Text(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static int? Integer(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var value) && value is > 0 and <= ushort.MaxValue ? value : -1;
        }

        return null;
    }

    private static int ParsePort(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= ushort.MaxValue ? port : -1;

    private static PeerNodeInfo? Peer(string? nodeId, string? region, string? host, int quicPort, int internalPort)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(region) || string.IsNullOrWhiteSpace(host)
            || quicPort is <= 0 or > ushort.MaxValue || internalPort is <= 0 or > ushort.MaxValue
            || !ValidSegment(nodeId) || !ValidSegment(region))
        {
            return null;
        }

        return new PeerNodeInfo(nodeId, region, host, quicPort, internalPort);
    }

    private static bool ValidSegment(string value) => !value.AsSpan().ContainsAnyExcept(SegmentCharacters);

    private static PeerDiscoveryOutcome Distinct(List<PeerNodeInfo> peers) =>
        peers.Select(static peer => peer.NodeId).Distinct(StringComparer.Ordinal).Count() == peers.Count
            ? peers
            : Invalid("peer node ids must be unique");

    private static ClusterConfigurationError Invalid(string message) => new(message, "cluster_peers");
}
