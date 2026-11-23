using System.Buffers;
using System.Text;
using ZLogger;

namespace TerraScale.Logging;

internal sealed class TerraScaleLogProcessor(TerraScaleLogFormatter formatter, TextWriter? output) : IAsyncLogProcessor
{
    [ThreadStatic]
    private static ArrayBufferWriter<byte>? _spare;

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

    public async ValueTask DisposeAsync()
    {
        try
        {
            await (output ?? Console.Out).FlushAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
    }

    private void Write(ReadOnlySpan<byte> bytes)
    {
        var chars = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(bytes.Length));
        try
        {
            var count = Encoding.UTF8.GetChars(bytes, chars);
            (output ?? Console.Out).Write(chars.AsSpan(0, count));
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }
}
