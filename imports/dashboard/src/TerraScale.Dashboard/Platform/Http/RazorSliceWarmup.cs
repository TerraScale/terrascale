using System.Reflection;
using RazorSlices;

namespace TerraScale.Dashboard.Platform.Http;

// RazorSlices 0.12.x shares one NullabilityInfoContext, which is not thread-safe, across every slice type's static
// initializer: two different slices first rendered at once can corrupt it and poison a slice type for the life of the
// process. Creating every generated slice once, on one thread, before the host serves requests leaves no initializer
// to race. Delete this when a RazorSlices release creates the context per call (upstream main since #154).
public static class RazorSliceWarmup
{
    private static readonly Lazy<int> Warmed = new(WarmAll, LazyThreadSafetyMode.ExecutionAndPublication);

    // Returns how many slices were initialized; hosts built in parallel share the one run.
    public static int EnsureWarmed() => Warmed.Value;

    private static int WarmAll()
    {
        var slices = 0;
        foreach (var proxy in typeof(RazorSliceWarmup).Assembly.GetTypes().Where(IsProxy))
        {
            var create = proxy.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException($"Slice proxy {proxy.Name} has no Create method.");
            var model = create.GetParameters().Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
            create.Invoke(null, model);
            slices++;
        }
        return slices;
    }

    private static bool IsProxy(Type type) => type.GetInterfaces().Any(i =>
        i == typeof(IRazorSliceProxy) || i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRazorSliceProxy<>));
}
