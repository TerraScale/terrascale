using TerraScale.Dashboard.Features.Backups;

namespace TerraScale.Dashboard.Features.Settings
{
    public static class SettingsEndpoints
    {
        public static IServiceCollection AddSettingsFeatures(this IServiceCollection services)
        {
            return services.AddSingleton<ISettingsBackend, SettingsBackend>().AddSingleton<SettingsService>();
        }
    }
}
