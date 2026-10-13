namespace TerraBase.Features.Import;

/// <summary>Registers one-time PostgreSQL and Turso imports (docs/terrabase/import.md). Always on: nothing is configured.</summary>
public static class ImportExtensions
{
    /// <summary>Registers <see cref="IImportJobs"/>, the staging area and the runner.</summary>
    public static IServiceCollection AddTerraBaseImport(this IServiceCollection services)
    {
        services.AddSingleton<ImportStaging>();
        services.AddSingleton<ImportQueue>();
        services.AddSingleton<IImportQueue>(provider => provider.GetRequiredService<ImportQueue>());
        services.AddSingleton<ImportJobs>();
        services.AddSingleton<IImportJobs>(provider => provider.GetRequiredService<ImportJobs>());
        services.AddHostedService<ImportRunner>();
        return services;
    }
}
