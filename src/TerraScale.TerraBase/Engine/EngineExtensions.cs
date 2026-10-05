using TerraScale.TerraBase.Configuration;
using TerraScale.TerraBase.Engine.Sqlite;

namespace TerraScale.TerraBase.Engine;

/// <summary>Registers the storage engine.</summary>
public static class EngineExtensions
{
    /// <summary>Registers the SQLite engine over <see cref="TerraBaseOptions.DataDirectory"/> as <see cref="IStorageEngine"/>.</summary>
    public static IServiceCollection AddTerraBaseEngine(this IServiceCollection services, TerraBaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // A factory registration lets the container own, and dispose, the engine.
        return services.AddSingleton<IStorageEngine>(provider => new SqliteStorageEngine(
            options.DataDirectory, options.ShutdownGrace, provider.GetRequiredService<ILogger<SqliteStorageEngine>>(), options.ReadinessCacheTtl));
    }
}
