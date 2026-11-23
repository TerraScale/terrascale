using System.Reflection;
using RazorSlices;

namespace TerraScale.Dashboard.Platform.Http
{
    public static class RazorSliceWarmup
    {
        private static readonly Lazy<int> Warmed = new(WarmAll, LazyThreadSafetyMode.ExecutionAndPublication);

        public static int EnsureWarmed()
        {
            return Warmed.Value;
        }

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

        private static bool IsProxy(Type type)
        {
            return type.GetInterfaces().Any(i =>
            i == typeof(IRazorSliceProxy) || i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRazorSliceProxy<>));
        }
    }
}
