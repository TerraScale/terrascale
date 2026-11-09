using System.Reflection.Metadata;

[assembly: MetadataUpdateHandler(typeof(TerraScale.Dashboard.Platform.Development.DevelopmentUpdates))]

namespace TerraScale.Dashboard.Platform.Development
{
    public static class DevelopmentUpdates
    {
        private static long revision;
        public static string BootId { get; } = Guid.NewGuid().ToString("N");

        public static long Revision => Interlocked.Read(ref revision);

        public static void ClearCache(Type[]? _) { }
        public static void UpdateApplication(Type[]? _)
        {
            Interlocked.Increment(ref revision);
        }
    }
}
