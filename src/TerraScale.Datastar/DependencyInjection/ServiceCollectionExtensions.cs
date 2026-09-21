using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace TerraScale.Datastar.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IDatastarBuilder AddDatastar(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<DatastarJsonOptions>();
        services.AddHttpContextAccessor();
        services.AddScoped<IDatastarService, DatastarService>();
        return new DatastarBuilder(services);
    }

    public static IDatastarBuilder AddJsonOptions(this IDatastarBuilder builder, Action<JsonSerializerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        builder.Services.Configure<DatastarJsonOptions>(options => configure(options.SignalsJsonSerializerOptions));
        return builder;
    }
}
