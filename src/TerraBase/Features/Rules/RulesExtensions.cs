using TerraBase.Features.Rows;

namespace TerraBase.Features.Rules;

internal static class RulesExtensions
{
    public static IServiceCollection AddTerraBaseRules(this IServiceCollection services) =>
        services.AddSingleton<RulesService>().AddSingleton<IRules>(provider => provider.GetRequiredService<RulesService>())
            .AddSingleton<RowsService>().AddSingleton<RowsRouteServices>();
}
