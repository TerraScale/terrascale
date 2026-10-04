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
        return services.AddSingleton<IStorageEngine>(new SqliteStorageEngine(options.DataDirectory));
    }
}
