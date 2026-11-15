using System.Diagnostics.Metrics;

namespace TerraBase.Hosting;

internal static class CounterSeries
{
    public static void Zero(Counter<long> counter) => counter.Add(0);

    public static void Zero(Counter<long> counter, string key, params ReadOnlySpan<string> values)
    {
        foreach (var value in values)
        {
            counter.Add(0, new KeyValuePair<string, object?>(key, value));
        }
    }

    public static void Zero(Counter<long> counter, string leftKey, ReadOnlySpan<string> left, string rightKey, ReadOnlySpan<string> right)
    {
        foreach (var first in left)
        {
            foreach (var second in right)
            {
                counter.Add(0, new KeyValuePair<string, object?>(leftKey, first), new KeyValuePair<string, object?>(rightKey, second));
            }
        }
    }
}
