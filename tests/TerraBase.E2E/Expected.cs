namespace TerraBase.E2E;

/// <summary>
/// Strict expected failure for a whole scenario of the replication design (docs/terrabase/multi-region-replication.md,
/// section 20): the test passes while the scenario fails and logs why, and fails once the scenario passes, so the slice
/// that lands the feature removes the marker. TUnit has no expected-failure attribute.
/// </summary>
internal static class Expected
{
    public static async Task FailureAsync(string gap, Func<Task> scenario)
    {
        try
        {
            await scenario();
        }
        catch (Exception error) when (error is not TUnit.Core.Exceptions.SkipTestException)
        {
            Console.WriteLine($"expected failure until {gap}: {error.GetType().Name}: {error.Message}");
            return;
        }

        throw new InvalidOperationException($"{gap} has landed: the scenario passes, so remove Expected.FailureAsync and run it directly");
    }

    // A scenario's own assertion: a failed check fails the scenario, not the test.
    public static void Check(bool holds, string what)
    {
        if (!holds)
        {
            throw new ScenarioException(what);
        }
    }
}

/// <summary>A scenario assertion that did not hold.</summary>
internal sealed class ScenarioException(string message) : Exception(message);
