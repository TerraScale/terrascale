using System.Net;

namespace TerraBase.E2E;

/// <summary>
/// Strict expected failure for a whole scenario of the replication design (docs/terrabase/multi-region-replication.md,
/// section 20): the test passes while the scenario fails the way its gap names, and fails once the scenario passes, so the
/// slice that lands the feature removes the marker. Any other failure (a 5xx, a crash, a lock, a harness or setup error)
/// fails the test: it is not the gap. TUnit has no expected-failure attribute.
/// </summary>
internal static class Expected
{
    // Answers how the scenario failed: its own way, carrying the gap's signature.
    public static async Task<Exception> FailureAsync(string gap, string signature, Func<Task> scenario)
    {
        try
        {
            await scenario();
        }
        catch (Exception error) when (error is not TUnit.Core.Exceptions.SkipTestException)
        {
            if (!Known(error, signature))
            {
                throw new UnexpectedFailureException(
                    $"{gap}: the scenario failed in a way its gap does not name (expected a scenario failure containing \"{signature}\"): {error.GetType().Name}: {error.Message}", error);
            }

            Console.WriteLine($"expected failure until {gap}: {error.GetType().Name}: {error.Message}");
            return error;
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

    // The scenario's own ways to fail: one of its checks or assertions, a convergence that never came, an answer the slice
    // will change (4xx, 501), or a hook not wired yet (reported as not supported). A 5xx or any other exception never is.
    internal static bool Known(Exception error, string signature) => error switch
    {
        AggregateException { InnerExceptions.Count: 1 } one => Known(one.InnerExceptions[0], signature),
        NotSupportedException => true,
        ScenarioException or NotConvergedException or TUnit.Assertions.Exceptions.AssertionException => error.Message.Contains(signature, StringComparison.Ordinal),
        HttpRequestException { StatusCode: { } status } when (int)status is >= 400 and < 500 || status == HttpStatusCode.NotImplemented =>
            error.Message.Contains(signature, StringComparison.Ordinal),
        _ => false,
    };
}

/// <summary>A scenario assertion that did not hold.</summary>
internal sealed class ScenarioException(string message) : Exception(message);

/// <summary>A listed scenario that failed some other way than its gap: a real failure, never an expected one.</summary>
internal sealed class UnexpectedFailureException(string message, Exception inner) : Exception(message, inner);
