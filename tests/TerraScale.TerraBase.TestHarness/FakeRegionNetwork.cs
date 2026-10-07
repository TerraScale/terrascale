using System.Runtime.CompilerServices;
using TerraBase.Features.CrossRegion;

namespace TerraBase.Testing;

/// <summary>
/// Core NATS request/reply between regions, in memory, shared by the servers of a test: every responder of a subject
/// answers a scattered request, one answers a single request, and a request to a subject nobody serves fails as
/// NATS's no-responders does. <see cref="Partition"/> cuts two cells apart, as a dropped leafnode link would, until
/// <see cref="Heal"/>.
/// </summary>
internal sealed class FakeRegionNetwork
{
    private readonly Lock _gate = new();
    private readonly List<(string Cell, string Subject, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>> Answer)> _responders = [];
    private readonly HashSet<(string, string)> _cut = [];

    /// <summary>The bus of a server in <paramref name="cell"/>.</summary>
    public IRegionBus Join(string cell) => new Bus(this, cell);

    /// <summary>Requests between <paramref name="a"/> and <paramref name="b"/> find no responder from now on.</summary>
    public void Partition(string a, string b)
    {
        lock (_gate)
        {
            _ = _cut.Add((a, b));
            _ = _cut.Add((b, a));
        }
    }

    /// <summary>Every cell reaches every other again.</summary>
    public void Heal()
    {
        lock (_gate)
        {
            _cut.Clear();
        }
    }

    private List<Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>>> Responders(string from, string subject)
    {
        lock (_gate)
        {
            return [.. _responders.Where(responder => responder.Subject == subject && !_cut.Contains((from, responder.Cell))).Select(responder => responder.Answer)];
        }
    }

    private sealed class Bus(FakeRegionNetwork network, string cell) : IRegionBus
    {
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> RequestManyAsync(string subject, ReadOnlyMemory<byte> request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var answer in network.Responders(cell, subject))
            {
                yield return await answer(request.ToArray(), cancellationToken);
            }
        }

        public async ValueTask<ReadOnlyMemory<byte>> RequestAsync(string subject, ReadOnlyMemory<byte> request, CancellationToken cancellationToken) =>
            network.Responders(cell, subject) is [var answer, ..]
                ? await answer(request.ToArray(), cancellationToken)
                : throw new IOException("no responders");

        public async Task ServeAsync(string subject, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>> answer, CancellationToken cancellationToken)
        {
            var responder = (cell, subject, answer);
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
