using System.Runtime.CompilerServices;

namespace TerraBase.Analyzers.BuildCheck;

internal readonly union ProbeResult(Success, Failure);
internal sealed record Success;
internal sealed record Failure;

// These methods are compiled, never executed. Each marker requires TB0001 at that exact line.
internal static class BuildCheck
{
    private static ProbeResult Get() => new Success();

    private static Task<ProbeResult> Later() => Task.FromResult<ProbeResult>(new Success());

    private static Task<ProbeResult?> NullableLater() => Task.FromResult<ProbeResult?>(new Success());

    private static ConfiguredValueTaskAwaitable<ProbeResult?> ConfiguredNullable() =>
        new ValueTask<ProbeResult?>(new Success()).ConfigureAwait(false);

    internal sealed class Holder(ProbeResult result)
    {
        internal ProbeResult Get() => result;

        internal ConfiguredValueTaskAwaitable<ProbeResult> Configured() =>
            new ValueTask<ProbeResult>(result).ConfigureAwait(false);
    }

    internal sealed class DerivedTask() : Task<ProbeResult>(() => new Success());

    private static DerivedTask Derived() => new();

    internal static void Probe(Holder? holder)
    {
        Get(); // TB0001
        Later(); // TB0001
        Later().ConfigureAwait(false); // TB0001
        NullableLater(); // TB0001
        NullableLater().ConfigureAwait(false); // TB0001
        ConfiguredNullable(); // TB0001
        holder?.Get(); // TB0001
        holder?.Configured(); // TB0001
        Derived(); // TB0001

        _ = Get();
        _ = Later();
        var kept = Get();
        _ = kept;
    }

    internal static async Task ProbeAwaitAsync()
    {
        await Later().ConfigureAwait(false); // TB0001
        await NullableLater().ConfigureAwait(false); // TB0001
        await ConfiguredNullable(); // TB0001
        await Derived().ConfigureAwait(false); // TB0001

        _ = await Later().ConfigureAwait(false);
        var kept = await Later().ConfigureAwait(false);
        _ = kept;
    }

    internal static void ProbeGeneric<T>(Func<T> call) where T : IUnion
    {
        call(); // TB0001
        _ = call();
    }

    internal static void ProbeGenericTask<T>(Func<T> call) where T : Task<ProbeResult>
    {
        call(); // TB0001
        _ = call();
    }

    internal sealed class RecursiveTask() : Task<RecursiveTask>(() => throw new InvalidOperationException());
    internal sealed class Expanding<T>() : Task<Expanding<Expanding<T>>>(() => throw new InvalidOperationException());

    internal static void ProbeNonUnion(Func<RecursiveTask> recursive, Func<Expanding<int>> expanding, Func<int> ordinary)
    {
        recursive();
        expanding();
        ordinary();
    }
}
