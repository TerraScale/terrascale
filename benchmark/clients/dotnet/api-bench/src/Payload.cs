using System.Text.Json;

namespace ApiBench;

/// <summary>Document bodies and query filters shared by the scenarios.</summary>
internal static class Payload
{
    internal const int Groups = 16;

    internal static readonly JsonElement[] Filters = [.. Enumerable.Range(0, Groups).Select(group => Parse($"{{\"eq\":[\"/g\",{group}]}}"))];

    /// <summary>Seeded collections are named for their payload; query.small always reads the 256-byte seed.</summary>
    internal static int SizeFor(string scenario, int payloadBytes) => scenario switch
    {
        "auth.token" => 0,
        "query.small" => 256,
        _ => payloadBytes,
    };

    /// <summary>A document of exactly <paramref name="bytes"/> UTF-8 bytes: {"g":group,"p":"xxx"}.</summary>
    internal static string Json(int group, int bytes)
    {
        var head = $"{{\"g\":{group},\"p\":\"";
        return head + new string('x', bytes - head.Length - 2) + "\"}";
    }

    internal static int RandomGroup() => Random.Shared.Next(Groups);

    internal static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
