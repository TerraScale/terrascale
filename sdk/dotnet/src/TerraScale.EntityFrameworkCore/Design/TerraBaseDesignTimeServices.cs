using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.EntityFrameworkCore.Design;
using TerraScale.EntityFrameworkCore.Infrastructure;

[assembly: DesignTimeProviderServices("TerraScale.EntityFrameworkCore.Design.TerraBaseDesignTimeServices")]

namespace TerraScale.EntityFrameworkCore.Design;

internal sealed class TerraBaseDesignTimeServices : IDesignTimeServices
{
    public void ConfigureDesignTimeServices(IServiceCollection services)
    {
        services.AddEntityFrameworkTerraBase();
        new EntityFrameworkRelationalDesignServicesBuilder(services).TryAddCoreServices();
    }
}
