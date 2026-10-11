using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace TerraBase.Features.Backup;

// Registered only by the selected saturation fixture; phase scopes retain no object names, keys or streams.
internal sealed class BackupReadObservation
{
    private const int SlotsPerPhase = 16;
    private readonly long[] _slots = new long[SlotsPerPhase * 8];
    private readonly long[] _starts = new long[8];
    private long _overflow;

    internal enum Phase
    {
        Fetch,
        Body,
        Read,
        Key,
        Decode,
        Output,
        ReplayWait,
        Replay,
    }

    internal Scope Enter(Phase phase)
    {
        Interlocked.Increment(ref _starts[(int)phase]);
        var now = Stopwatch.GetTimestamp();
        for (var slot = (int)phase * SlotsPerPhase; slot < ((int)phase + 1) * SlotsPerPhase; slot++)
        {
            if (Interlocked.CompareExchange(ref _slots[slot], now, 0) == 0)
            {
                return new(this, slot);
            }
        }

        Interlocked.Increment(ref _overflow);
        return default;
    }

    // Every phase reports active/start-count/oldest-active-ms; overflow makes incomplete ownership explicit.
    internal string Progress()
    {
        var now = Stopwatch.GetTimestamp();
        var text = new StringBuilder(192);
        foreach (var phase in Enum.GetValues<Phase>())
        {
            var active = 0;
            var oldest = now;
            for (var slot = (int)phase * SlotsPerPhase; slot < ((int)phase + 1) * SlotsPerPhase; slot++)
            {
                var started = Volatile.Read(ref _slots[slot]);
                if (started > 0)
                {
                    active++;
                    oldest = Math.Min(oldest, started);
                }
            }

            text.Append(CultureInfo.InvariantCulture, $"{phase}={active}/{Volatile.Read(ref _starts[(int)phase])}/{(long)((now - oldest) * 1000d / Stopwatch.Frequency)} ");
        }

        text.Append(CultureInfo.InvariantCulture, $"overflow={Volatile.Read(ref _overflow)}");
        return text.ToString();
    }

    internal readonly struct Scope(BackupReadObservation? owner, int slot) : IDisposable
    {
        // A paused scope keeps its reserved slot so another operation cannot take it before Resume.
        internal void Pause()
        {
            if (owner is not null)
            {
                Interlocked.Exchange(ref owner._slots[slot], -1);
            }
        }

        internal void Resume()
        {
            if (owner is not null)
            {
                Interlocked.Increment(ref owner._starts[slot / SlotsPerPhase]);
                Interlocked.Exchange(ref owner._slots[slot], Stopwatch.GetTimestamp());
            }
        }

        public void Dispose()
        {
            if (owner is not null)
            {
                Interlocked.Exchange(ref owner._slots[slot], 0);
            }
        }
    }
}
