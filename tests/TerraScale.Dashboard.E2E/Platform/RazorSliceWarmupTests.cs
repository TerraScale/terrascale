using System.Reflection;
using System.Runtime.Loader;

namespace TerraScale.Dashboard.E2E.Platform;

// Every slice is initialized before a host serves, so cold concurrent first renders never share RazorSlices'
// non-thread-safe initializer state. Each round loads fresh copies of the dashboard and RazorSlices, so the slice
// initializers are cold again, as in a newly started process.
public sealed class RazorSliceWarmupTests
{
    [Test]
    public async Task WarmupCoversEverySliceFile()
    {
        var root = Path.Combine(DashboardE2EFixture.FindRoot(), "src/TerraScale.Dashboard");
        var files = Directory.EnumerateFiles(root, "*.cshtml", SearchOption.AllDirectories)
            .Count(f => !f.Contains("/node_modules/", StringComparison.Ordinal) && Path.GetFileName(f) != "_ViewImports.cshtml");
        await Assert.That(RazorSliceWarmup.EnsureWarmed()).IsEqualTo(files);
    }

    [Test]
    public async Task ColdConcurrentHostsAndRendersNeverPoisonASlice()
    {
        for (var round = 0; round < 10; round++)
        {
            var context = new FreshDashboard();
            try
            {
                var assembly = context.LoadFromAssemblyName(typeof(RazorSliceWarmup).Assembly.GetName());
                var warm = assembly.GetType(typeof(RazorSliceWarmup).FullName!)!.GetMethod(nameof(RazorSliceWarmup.EnsureWarmed))!;
                var proxies = assembly.GetTypes()
                    .Where(t => t.GetInterfaces().Any(i => i.Name.StartsWith("IRazorSliceProxy", StringComparison.Ordinal)))
                    .Select(t => t.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!)
                    .ToArray();
                using var start = new Barrier(Environment.ProcessorCount * 2);
                // Each worker is a host: it warms up, then renders every slice at once with the others.
                var counts = await Task.WhenAll(Enumerable.Range(0, start.ParticipantCount).Select(worker => Task.Factory.StartNew(() =>
                {
                    start.SignalAndWait();
                    var warmed = (int)warm.Invoke(null, null)!;
                    // Rotated per worker, so different slice types initialize at the same moment.
                    foreach (var create in proxies.Skip(worker * 7 % proxies.Length).Concat(proxies.Take(worker * 7 % proxies.Length)))
                        create.Invoke(null, create.GetParameters().Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray());
                    return warmed;
                }, TaskCreationOptions.LongRunning)));
                await Assert.That(counts.Distinct().Single()).IsEqualTo(proxies.Length);
            }
            finally
            {
                context.Unload();
            }
        }
    }

    private sealed class FreshDashboard() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) =>
            name.Name is "TerraScale.Dashboard" or "RazorSlices"
                ? LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, name.Name + ".dll"))
                : null;
    }
}
