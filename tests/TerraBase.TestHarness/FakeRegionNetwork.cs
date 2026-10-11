using System.Runtime.CompilerServices;
using TerraBase.Features.CrossRegion;

namespace TerraBase.Testing;

/// <summary>
/// Core NATS request/reply between regions, in memory, shared by the servers of a test, as ts-orchestrator's interregion
/// link serves it: a request to another cell gets one reply (the link imports each cell's <c>query</c> subjects as a
/// Singleton service), and a request to a subject nobody serves fails as NATS's no-responders does. Within a cell every
/// responder answers a scattered request. <see cref="Partition"/> cuts two cells apart, as a dropped link would, until
/// <see cref="Heal"/>. <see cref="Faults"/> makes the link lossy.
/// </summary>
internal sealed class FakeRegionNetwork
{
    private const int MaxPublishedPerSubject = 64;
    private readonly Lock _gate = new();
    private Random _random = new(0);
    private RegionFaults? _faults;
    private int _dropped;
    private int _corrupted;

    /// <summary>Requests or replies <see cref="Faults"/> lost so far.</summary>
    public int Dropped => Volatile.Read(ref _dropped);

    /// <summary>Replies <see cref="Faults"/> corrupted so far.</summary>
    public int Corrupted => Volatile.Read(ref _corrupted);
    private readonly List<(string Cell, string? Node, string Subject, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>> Answer)> _responders = [];
    private readonly HashSet<(string, string)> _cut = [];
    private readonly HashSet<string> _muted = [with(StringComparer.Ordinal)];
    private readonly Dictionary<string, Queue<byte[]>> _published = [with(StringComparer.Ordinal)];

    /// <summary>The bus of a server in <paramref name="cell"/>, named <paramref name="node"/> for <see cref="Mute"/>.</summary>
    public IRegionBus Join(string cell, string? node = null) => new Bus(this, cell, node);

    /// <summary>The server joined as <paramref name="node"/> answers no request until <see cref="Heal"/>, as a member cut off from the link.</summary>
    public void Mute(string node)
    {
        lock (_gate)
        {
            _ = _muted.Add(node);
        }
    }

    /// <summary>Requests between <paramref name="a"/> and <paramref name="b"/> find no responder from now on.</summary>
    public void Partition(string a, string b)
    {
        lock (_gate)
        {
            _ = _cut.Add((a, b));
            _ = _cut.Add((b, a));
        }
    }

    /// <summary>
    /// Faults on every request between cells from now on, or none; the seed makes a run repeatable.
    /// Each enabled drop or corruption occurs at least once on this network, then follows its probability.
    /// </summary>
    public RegionFaults? Faults
    {
        get => Volatile.Read(ref _faults);
        set
        {
            lock (_gate)
            {
                _random = new Random(value?.Seed ?? 0);
                _faults = value;
            }
        }
    }

    /// <summary>Every cell reaches every other again.</summary>
    public void Heal()
    {
        lock (_gate)
        {
            _cut.Clear();
            _muted.Clear();
        }
    }

    /// <summary>The last publishes on <paramref name="subject"/>, oldest first; the last 64 are kept.</summary>
    public IReadOnlyList<byte[]> Published(string subject)
    {
        lock (_gate)
        {
            return _published.TryGetValue(subject, out var messages) ? [.. messages] : [];
        }
    }

    private void Record(string subject, byte[] data)
    {
        lock (_gate)
        {
            if (!_published.TryGetValue(subject, out var messages))
            {
                messages = [];
                _published[subject] = messages;
            }

            if (messages.Count == MaxPublishedPerSubject)
            {
                _ = messages.Dequeue();
            }

            messages.Enqueue(data);
        }
    }

    private List<Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>>> Responders(string from, string subject)
    {
        lock (_gate)
        {
            var responders = _responders.Where(responder => responder.Subject == subject && !_cut.Contains((from, responder.Cell))
                && (responder.Node is null || !_muted.Contains(responder.Node))).ToList();
            return [.. responders.Take(responders.Any(responder => responder.Cell != from) ? 1 : responders.Count).Select(responder => responder.Answer)];
        }
    }

    // The reply of answer, through the faults: null when the request or its reply is lost.
    private async ValueTask<byte[]?> CallAsync(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>> answer, ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
    {
        double drop, duplicate, corrupt, delay, position;
        RegionFaults? faults;
        lock (_gate)
        {
            faults = _faults;
            (drop, duplicate, corrupt, delay, position) = (_random.NextDouble(), _random.NextDouble(), _random.NextDouble(), _random.NextDouble(), _random.NextDouble());
        }

        if (faults is null)
        {
            return await answer(request.ToArray(), cancellationToken);
        }

        await Task.Delay(faults.MaxDelay * delay, cancellationToken);
        // Finite random runs can miss a fault; consume its mandatory occurrence only when it actually happens.
        lock (_gate)
        {
            if (drop < faults.Drop || (faults.Drop > 0 && _dropped == 0))
            {
                _ = Interlocked.Increment(ref _dropped);
                return null;
            }
        }

        var reply = await answer(request.ToArray(), cancellationToken);
        if (duplicate < faults.Duplicate)
        {
            // The responder got the request twice; the asking member takes the second answer.
            reply = await answer(request.ToArray(), cancellationToken);
        }

        lock (_gate)
        {
            if (reply.Length > 0 && (corrupt < faults.Corrupt || (faults.Corrupt > 0 && _corrupted == 0)))
            {
                reply = [.. reply];
                reply[(int)(position * reply.Length)] ^= 0x5a;
                _ = Interlocked.Increment(ref _corrupted);
            }
        }

        return reply;
    }

    private sealed class Bus(FakeRegionNetwork network, string cell, string? node) : IRegionBus
    {
        public string? Cell => cell;

        public ValueTask PublishAsync(string subject, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            network.Record(subject, data.ToArray());
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> RequestManyAsync(string subject, ReadOnlyMemory<byte> request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var answer in network.Responders(cell, subject))
            {
                if (await network.CallAsync(answer, request, cancellationToken) is { } reply)
                {
                    yield return reply;
                }
            }
        }

        public async ValueTask<ReadOnlyMemory<byte>> RequestAsync(string subject, ReadOnlyMemory<byte> request, CancellationToken cancellationToken) =>
            network.Responders(cell, subject) is [var answer, ..]
                ? await network.CallAsync(answer, request, cancellationToken) ?? throw new IOException("the request timed out")
                : throw new IOException("no responders");

        public async Task ServeAsync(string subject, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>> answer, CancellationToken cancellationToken)
        {
            // As on NATS, a responder answers within its own lifetime, not the asking member's: it stops with its server.
            async ValueTask<byte[]> ServedAsync(ReadOnlyMemory<byte> request, CancellationToken asking)
            {
                return await answer(request, cancellationToken).AsTask().WaitAsync(asking);
            }

            var responder = (cell, node, subject, (Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>>)ServedAsync);
            lock (network._gate)
            {
                network._responders.Add(responder);
            }

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                lock (network._gate)
                {
                    _ = network._responders.Remove(responder);
                }
            }
        }
    }
}

/// <summary>
/// Faults of a <see cref="FakeRegionNetwork"/>: the probability that a request or its reply is lost, that the responder
/// gets the request twice, and that one byte of the reply is corrupted, and the longest delay before an answer.
/// </summary>
internal sealed record RegionFaults(double Drop, double Duplicate, double Corrupt, TimeSpan MaxDelay, int Seed);
