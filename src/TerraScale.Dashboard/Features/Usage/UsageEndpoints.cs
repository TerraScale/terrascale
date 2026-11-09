using TerraScale.Dashboard.Features.Billing;

namespace TerraScale.Dashboard.Features.Usage
{
    public static class UsageEndpoints
    {
        public static IServiceCollection AddUsageFeatures(this IServiceCollection services)
        {
            return services.AddSingleton<SpendingService>()
            .AddSingleton<IUsageMeasurements, UsageBackend>().AddSingleton<UsageService>();
        }
    }
}
