namespace TerraBase.Slow.Replication;

internal sealed partial class FaultSoakCluster
{
    public int NetworkResets => _lossy.Sum(link => link.Resets);
    public string ClockSkews => string.Join(",", Regions.Select(region => $"{region}={_nodes[region].Skew}ms"));

    public static Task<FaultSoakCluster> StartLocalAsync(int seed, CancellationToken cancellationToken) =>
        StartAsync(seed, ["gru"], cancellationToken);

    public void Partition(string a, string b)
    {
        foreach (var edge in new[] { (a, b), (b, a) })
        {
            _cuts.Add(edge);
            if (_links.TryGetValue(edge, out var link))
            {
                link.Cut = true;
            }
        }
    }

    public void Heal()
    {
        _cuts.Clear();
        _networkFaults.Clear();
        foreach (var link in _links.Values)
        {
            link.Via = null;
            link.Cut = false;
            link.Reset();
        }
    }

    public Task LossyAsync(string a, string b, TimeSpan maxDelay, int bytesPerSecond, double resetChance)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytesPerSecond, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDelay, TimeSpan.Zero);
        if (resetChance is < 0 or > 1 || double.IsNaN(resetChance))
        {
            throw new ArgumentOutOfRangeException(nameof(resetChance));
        }

        foreach (var edge in new[] { (a, b), (b, a) })
        {
            _networkFaults[edge] = new(maxDelay, bytesPerSecond, resetChance);
            var link = _links[edge];
            var lossy = new TerraBase.E2E.LossyLink(link.Target, maxDelay, bytesPerSecond, resetChance, unchecked(_seed + _lossy.Count)).Start();
            _lossy.Add(lossy);
            link.Via = lossy.Endpoint;
            link.Reset();
        }

        return Task.CompletedTask;
    }

    public void SlowDisk(string region, int milliseconds) => Faults(region).Delay(TimeSpan.FromMilliseconds(milliseconds));

    public long DelayedSyncs(string region)
    {
        var path = Path.Combine(Faults(region).Directory, "delayed");
        return File.Exists(path) ? File.ReadLines(path).LongCount() : 0;
    }

    public void HealDisk(string region) => Faults(region).Clear();

    public void DropUnsynced(string region)
    {
        var node = _nodes[region];
        if (node.Client is not null || node.Process is { HasExited: false })
        {
            throw new InvalidOperationException($"{region} must be stopped before dropping unsynced WAL bytes");
        }

        Faults(region).DropUnsynced(node.Data);
    }
}
