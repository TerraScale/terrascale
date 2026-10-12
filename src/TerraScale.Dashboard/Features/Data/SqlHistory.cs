using System.Text;
using System.Text.Json;

namespace TerraScale.Dashboard.Features.Data;

public static class SqlHistory
{
    public static IReadOnlyList<string> Read(string json)
    {
        if (json.Length == 0 || Encoding.UTF8.GetByteCount(json) > 4096) return [];
        try { return JsonSerializer.Deserialize<string[]>(json)?.Where(statement => !string.IsNullOrWhiteSpace(statement)).Take(20).ToArray() ?? []; }
        catch (JsonException) { return []; }
    }

    public static IReadOnlyList<string> Run(IReadOnlyList<string> history, string statement)
    {
        if (string.IsNullOrWhiteSpace(statement) || Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new[] { statement })) > 4096) return history;
        var result = new List<string> { statement };
        foreach (var previous in history.Where(previous => previous != statement).Take(19))
        {
            result.Add(previous);
            if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result)) > 4096) { result.RemoveAt(result.Count - 1); break; }
        }
        return result;
    }
    public static string Label(string statement)
    {
        var label = string.Join(' ', statement.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return label.Length <= 80 ? label : label[..79] + "…";
    }
}
