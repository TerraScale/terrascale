using System.Buffers;
using System.Text;
using ZLogger;

namespace TerraScale.Logging;

/// <summary>
/// Formats each entry on the calling thread, so <c>Activity.Current</c> is still the caller's, then writes the line.
/// There is no queue: a record is written by the call that logged it, in order.
/// </summary>
/// <param name="output">Where records go; null means <see cref="Console.Out"/>, read at each write so <see cref="Console.SetOut"/> is honored.</param>
internal sealed class TerraScaleLogProcessor(TerraScaleLogFormatter formatter, TextWriter? output) : IAsyncLogProcessor
{
    // One buffer per thread, reused across records. Taken while in use so a re-entrant log call gets its own.
    [ThreadStatic]
    private static ArrayBufferWriter<byte>? _spare;

    /// <inheritdoc />
    public void Post(IZLoggerEntry log)
    {
        var buffer = _spare ?? new ArrayBufferWriter<byte>();
        _spare = null;
        try
        {
            buffer.Clear();
            try
            {
                formatter.FormatLogEntry(buffer, log);
            }
            finally
            {
                log.Return();
            }

            Write(buffer.WrittenSpan);
        }
        finally
        {
            _spare = buffer;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await (output ?? Console.Out).FlushAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // stdout is gone at shutdown; there is nothing left to report to.
        }
    }

    private void Write(ReadOnlySpan<byte> bytes)
    {
        // Console.Out and Console.SetOut writers are synchronized, so one Write per record needs no lock of ours.
        var chars = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(bytes.Length));
        try
        {
            var count = Encoding.UTF8.GetChars(bytes, chars);
            (output ?? Console.Out).Write(chars.AsSpan(0, count));
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // stdout is broken; the record is dropped rather than thrown into the caller, and nothing else can report it.
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }
}
