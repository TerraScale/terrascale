using System.Globalization;

namespace TerraScale.Dashboard.Features.Usage;

public static class SpendingForm
{
    public static bool TryParse(IFormCollection form, out SpendingChange change)
    {
        change = new();
        if (!Cap(form, "spendCap", out var cap, out var hasCap) || !Cap(form, "initial_spendCap", out var initialCap, out var hasInitialCap)) return false;
        long? organizationCap = hasCap && (!hasInitialCap || cap != initialCap) ? cap : null;
        bool? paidExcess = null;
        if (hasCap && form.TryGetValue("initial_paidExcess", out var original))
        {
            if (original.Count != 1 || original[0] is not ("true" or "false")) return false;
            var paid = false;
            if (form.TryGetValue("paidExcess", out var submitted))
            {
                if (submitted.Count != 1 || submitted[0] is not ("true" or "on" or "false" or "")) return false;
                paid = submitted[0] is "true" or "on";
            }
            if (paid != (original[0] == "true")) paidExcess = paid;
        }
        if (!Caps(form, "projCap_", out var projects) || !Caps(form, "dbCap_", out var databases) ||
            !Operations(form, "proj", out var projectOperations) || !Operations(form, "db", out var databaseOperations)) return false;
        change = new(organizationCap, paidExcess, projects, databases, projectOperations, databaseOperations);
        return true;
    }
    private static bool Cap(IFormCollection form, string key, out long cents, out bool present)
    {
        cents = 0; present = form.TryGetValue(key, out var values);
        if (!present) return true;
        if (values.Count != 1) return false;
        var text = values[0]?.Trim() ?? "";
        if (text.Length == 0) return true;
        var parts = text.Split('.');
        if (parts.Length > 2 || parts[0].Length == 0 || parts.Any(part => part.Any(character => !char.IsAsciiDigit(character))) || parts.Length == 2 && parts[1].Length is not (1 or 2) ||
            !decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var dollars) || dollars > long.MaxValue / 100m) return false;
        cents = (long)(dollars * 100); return true;
    }
    private static bool Caps(IFormCollection form, string prefix, out IReadOnlyDictionary<string, long> result)
    {
        var changes = new Dictionary<string, long>(StringComparer.Ordinal);
        result = changes;
        foreach (var key in form.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal) || key.StartsWith("initial_" + prefix, StringComparison.Ordinal)))
        {
            var initial = key.StartsWith("initial_", StringComparison.Ordinal);
            var id = key[(initial ? 8 + prefix.Length : prefix.Length)..];
            if (id.Length == 0 || !Cap(form, key, out var cap, out _)) return false;
            if (initial) continue;
            if (!Cap(form, "initial_" + key, out var before, out var hasBefore)) return false;
            if (!hasBefore || cap != before) changes[id] = cap;
        }
        return true;
    }
    private static bool Operations(IFormCollection form, string prefix, out IReadOnlyDictionary<string, OperationLimitChange> result)
    {
        var changes = new Dictionary<string, OperationLimitChange>(StringComparer.Ordinal); result = changes;
        foreach (var category in new[] { "Read", "Write", "Replication" })
        {
            var name = prefix + category + "_";
            foreach (var key in form.Keys.Where(key => key.StartsWith(name, StringComparison.Ordinal) || key.StartsWith("initial_" + name, StringComparison.Ordinal)))
            {
                var initial = key.StartsWith("initial_", StringComparison.Ordinal);
                var id = key[(initial ? 8 + name.Length : name.Length)..];
                if (id.Length == 0 || !Count(form, key, out var value, out _)) return false;
                if (initial) continue;
                if (!Count(form, "initial_" + key, out var before, out var hasBefore)) return false;
                if (hasBefore && value == before) continue;
                var current = changes.GetValueOrDefault(id) ?? new();
                changes[id] = category switch { "Read" => current with { ReadLimit = value }, "Write" => current with { WriteLimit = value }, _ => current with { ReplicationLimit = value } };
            }
        }
        return true;
    }
    private static bool Count(IFormCollection form, string key, out long value, out bool present)
    {
        value = 0; present = form.TryGetValue(key, out var values);
        if (!present) return true;
        if (values.Count != 1) return false;
        var text = values[0]?.Trim() ?? "";
        return text.Length == 0 || text.All(char.IsAsciiDigit) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
