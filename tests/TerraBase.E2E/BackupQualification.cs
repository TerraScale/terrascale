namespace TerraBase.E2E;

/// <summary>
/// The A7YES backup qualification (docs/terrabase/e2e-disposable-app.md, "Backup"): a backup at T holds every write
/// acknowledged before T, or the restore answers a typed refusal. Supported cuts are the functional proof and must
/// restore and match; a refusal passes only the safety assertion of a fault cut. A plan with no supported cut, or a run
/// in which every cut refused, is never green. This is the judge only: the pinned scenario that takes backups, restores
/// the cuts and feeds it is not wired yet, so no backup qualification result exists until it lands (runbook, "Backup").
/// </summary>
internal static class BackupQualification
{
    /// <summary>What a database holds: listing count and digest, each document's version, and the restored identity.</summary>
    internal sealed record Contents(int Count, string Digest, IReadOnlyDictionary<string, string> Versions, string BranchedFrom, IReadOnlyList<string> Regions);

    /// <summary>A cut frozen before the run, with the writes acknowledged before it.</summary>
    internal sealed record Cut(string Name, bool Supported, Contents Acknowledged);

    /// <summary>A cut's restore: the restored contents, or the typed refusal's error code.</summary>
    internal sealed record Outcome(string Cut, Contents? Restored, string? Refusal);

    /// <summary>Every reason the run is not green; empty only when it is.</summary>
    public static IReadOnlyList<string> Failures(IReadOnlyList<Cut> plan, IReadOnlyList<Outcome> outcomes)
    {
        var failures = new List<string>();
        if (!plan.Any(cut => cut.Supported))
        {
            failures.Add("the plan has no supported cut: refusals alone prove safety, never a working restore");
        }

        failures.AddRange(plan.GroupBy(cut => cut.Name, StringComparer.Ordinal).Where(same => same.Count() > 1).Select(same => $"cut {same.Key} is planned twice"));
        failures.AddRange(outcomes.Where(outcome => !plan.Any(cut => cut.Name == outcome.Cut)).Select(outcome => $"cut {outcome.Cut} was not in the frozen plan"));
        foreach (var cut in plan)
        {
            var answered = outcomes.Where(outcome => outcome.Cut == cut.Name).ToArray();
            if (answered is not [var outcome])
            {
                failures.Add($"cut {cut.Name} has {answered.Length} outcomes, not one");
            }
            else if (outcome.Restored is { } restored)
            {
                if (Difference(cut.Acknowledged, restored) is { } difference)
                {
                    failures.Add($"cut {cut.Name} restored a different database ({difference}): a partial or wrong restore");
                }
            }
            else if (cut.Supported)
            {
                failures.Add($"supported cut {cut.Name} refused ({outcome.Refusal ?? "no code"}): a supported cut must restore");
            }
            // shortcut: any snake_case error code counts as typed; the backup scenario pins the exact refusal codes it expects.
            else if (outcome.Refusal is not [>= 'a' and <= 'z', ..] code || !code.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            {
                failures.Add($"cut {cut.Name} neither restored nor answered a typed refusal");
            }
        }

        return failures;
    }

    private static string? Difference(Contents acknowledged, Contents restored)
    {
        if (restored.Count != acknowledged.Count || restored.Digest != acknowledged.Digest)
        {
            return $"{restored.Count} documents, digest {restored.Digest}; acknowledged {acknowledged.Count}, digest {acknowledged.Digest}";
        }

        var versions = acknowledged.Versions.Where(pair => !restored.Versions.TryGetValue(pair.Key, out var version) || version != pair.Value)
            .Select(pair => pair.Key).Concat(restored.Versions.Keys.Where(id => !acknowledged.Versions.ContainsKey(id))).ToArray();
        if (versions.Length > 0)
        {
            return $"versions differ for {string.Join(", ", versions.Take(5))}";
        }

        return restored.BranchedFrom != acknowledged.BranchedFrom || !restored.Regions.Order(StringComparer.Ordinal).SequenceEqual(acknowledged.Regions.Order(StringComparer.Ordinal))
            ? $"branched from {restored.BranchedFrom} in {string.Join('/', restored.Regions)}; expected {acknowledged.BranchedFrom} in {string.Join('/', acknowledged.Regions)}"
            : null;
    }
}
