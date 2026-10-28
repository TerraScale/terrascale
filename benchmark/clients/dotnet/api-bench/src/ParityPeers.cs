using System.Net;
using System.Net.Sockets;

namespace ApiBench;

internal static class ParityPeers
{
    internal static string[] Read()
    {
        var path = Environment.GetEnvironmentVariable("TS_PEERS_FILE");
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Parity needs TS_PEERS_FILE from ts-orch.");
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("Parity peers file could not be read.");
        }
        return Parse(text);
    }

    // Match the Go parity runner: reject the whole file if any non-comment row is malformed.
    internal static string[] Parse(string text)
    {
        var regions = new SortedSet<string>(StringComparer.Ordinal);
        using var reader = new StringReader(text);
        var number = 0;
        while (reader.ReadLine() is { } line)
        {
            number++;
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 3 || !Name(fields[0], region: false) || !Name(fields[1], region: true)
                || !IPAddress.TryParse(fields[2], out var address) || address.AddressFamily != AddressFamily.InterNetwork
                || !string.Equals(address.ToString(), fields[2], StringComparison.Ordinal))
                throw new InvalidDataException($"Parity peers file row {number} is not <node> <region> <ipv4>.");
            regions.Add(fields[1]);
        }
        foreach (var required in new[] { "gru", "sjc", "fra" })
            if (!regions.Contains(required)) throw new InvalidDataException($"Parity required serving region {required} is absent from the peers file.");
        return [.. regions];
    }

    private static bool Name(string value, bool region)
    {
        static bool AlphaNumeric(char c) => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c);
        return value.Length is >= 1 and <= 63 && AlphaNumeric(value[0]) && AlphaNumeric(value[^1])
            && (!region || char.IsAsciiLetterLower(value[0])) && value.All(c => AlphaNumeric(c) || c == '-');
    }
}
