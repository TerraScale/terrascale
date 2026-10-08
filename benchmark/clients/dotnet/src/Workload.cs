using System.Text.Json;

namespace BenchClient;

internal sealed record Workload(string Name, int DurationS, int WarmupS, int Concurrency,
    int Documents, int DocSizeBytes, IReadOnlyList<MixEntry> Mix, double P99Ms, Ramp? Ramp)
{
    internal const int MaxFileBytes = 1_048_576;
    private static readonly HashSet<string> Operations =
        ["doc.create", "doc.get", "doc.update", "doc.delete", "query.simple", "query.indexed", "txn.multi"];

    internal static async Task<Workload> ReadAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var bytes = new byte[MaxFileBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length));
            if (read == 0) break;
            length += read;
        }
        if (length > MaxFileBytes) Invalid("Workload file exceeds 1 MiB.");
        return Parse(bytes.AsMemory(0, length));
    }

    internal static Workload Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaxFileBytes) Invalid("Workload file exceeds 1 MiB.");
        _ = new System.Text.UTF8Encoding(false, true).GetCharCount(bytes.Span);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        Object(root, ["name", "duration_s", "warmup_s", "concurrency", "dataset", "mix", "targets"], ["ramp"]);
        var name = Text(root, "name");
        if (name.Length > 63 || name[0] is < 'a' or > 'z' ||
            name.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
            Invalid("name must be 1–63 lowercase letters, digits or hyphens, beginning with a letter.");

        var duration = Integer(root, "duration_s", 1, 600);
        var warmup = Integer(root, "warmup_s", 0, 60);
        var concurrency = Integer(root, "concurrency", 1, 256);
        var dataset = root.GetProperty("dataset");
        Object(dataset, ["documents", "doc_size_bytes"]);
        var documents = Integer(dataset, "documents", 1, 10_000);
        var size = Integer(dataset, "doc_size_bytes", 32, 1_048_576);
        var targets = root.GetProperty("targets");
        Object(targets, ["p99_ms"]);
        var target = targets.GetProperty("p99_ms");
        if (target.ValueKind != JsonValueKind.Number || !target.TryGetDouble(out var p99) || !double.IsFinite(p99) || p99 < 1 || p99 > 30_000)
            throw new PreflightException("INVALID_WORKLOAD", "p99_ms must be a finite number from 1 to 30000.");

        var mix = root.GetProperty("mix");
        if (mix.ValueKind != JsonValueKind.Array || mix.GetArrayLength() is < 1 or > 7)
            Invalid("mix must contain 1–7 distinct operations.");
        var entries = new List<MixEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in mix.EnumerateArray())
        {
            Object(item, ["op", "weight"], ["doc_size_bytes"]);
            var op = Text(item, "op");
            if (!Operations.Contains(op) || !seen.Add(op)) Invalid("mix contains an unsupported or repeated operation.");
            var weight = Integer(item, "weight", 1, 100);
            var payload = item.TryGetProperty("doc_size_bytes", out _) ? Integer(item, "doc_size_bytes", 32, 1_048_576) : size;
            entries.Add(new(op, weight, payload));
        }
        if (entries.Sum(entry => entry.Weight) != 100) Invalid("mix weights must sum to 100.");

        Ramp? ramp = null;
        if (root.TryGetProperty("ramp", out var config))
        {
            Object(config, ["step_s", "concurrency"]);
            var stepS = Integer(config, "step_s", 5, 600);
            var levels = config.GetProperty("concurrency");
            if (levels.ValueKind != JsonValueKind.Array || levels.GetArrayLength() is < 2 or > 7)
                Invalid("ramp.concurrency must contain 2–7 increasing steps.");
            var steps = levels.EnumerateArray().Select(value => Number(value, "ramp.concurrency", 1, 256)).ToArray();
            if (steps[0] != concurrency || steps.Where((value, index) => index > 0 && value <= steps[index - 1]).Any() ||
                stepS * steps.Length != duration)
                Invalid("Ramp steps must increase from base concurrency and total duration_s.");
            ramp = new(stepS, steps);
        }
        else if (name == "ramp") Invalid("The ramp workload requires ramp steps.");

        return new(name, duration, warmup, concurrency, documents, size, entries.AsReadOnly(), p99, ramp);
    }

    private static void Object(JsonElement value, string[] required, string[]? optional = null)
    {
        if (value.ValueKind != JsonValueKind.Object) Invalid("Expected a JSON object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!seen.Add(property.Name) || (!required.Contains(property.Name) && !(optional?.Contains(property.Name) ?? false)))
                Invalid("Workload contains an unknown or duplicate field.");
        if (required.Any(name => !seen.Contains(name))) Invalid("Workload is missing a required field.");
    }

    private static string Text(JsonElement value, string name)
    {
        var item = value.GetProperty(name);
        if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            Invalid($"{name} must be a nonempty string.");
        return item.GetString()!;
    }

    private static int Integer(JsonElement value, string name, int min, int max) => Number(value.GetProperty(name), name, min, max);

    private static int Number(JsonElement value, string name, int min, int max)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < min || number > max)
            throw new PreflightException("INVALID_WORKLOAD", $"{name} must be an integer from {min} to {max}.");
        return number;
    }

    private static void Invalid(string message) => throw new PreflightException("INVALID_WORKLOAD", message);
}

internal sealed record MixEntry(string Op, int Weight, int DocSizeBytes);
internal sealed record Ramp(int StepS, IReadOnlyList<int> Concurrency);
