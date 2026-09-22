using Microsoft.Extensions.DependencyInjection;
using TerraScale.Datastar.DependencyInjection;

namespace TerraScale.Datastar.ModelBinding;

public static class MvcServiceExtensions
{
    public static IDatastarBuilder AddDatastarMvc(this IDatastarBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddDatastarMvc();
        return builder;
    }

    public static IServiceCollection AddDatastarMvc(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IDatastarService)))
            throw new InvalidOperationException("AddDatastarMvc requires AddDatastar first.");
        services.AddControllers(options => options.ModelBinderProviders.Insert(0, new SignalsModelBinderProvider()));
        return services;
    }
}
