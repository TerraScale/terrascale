namespace TerraScale.Dashboard.Platform.Persistence
{
    // Public customer connection strings use TerraBase's global origin.
    public static class TerraBaseDiscovery
    {
        public static readonly Uri Origin = new("https://api.terrascale.tech/");
    }
}
