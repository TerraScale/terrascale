using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace TerraScale.EntityFrameworkCore;

/// <summary>
/// DbContext options helpers for TerraScale source integration.
/// </summary>
public static class TerraScaleDbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Adds TerraScale source metadata to a DbContext options builder.
    /// </summary>
    public static DbContextOptionsBuilder UseTerraScaleSource(
        this DbContextOptionsBuilder optionsBuilder,
        Action<TerraScaleEntityFrameworkCoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        var existingExtension = optionsBuilder.Options.FindExtension<TerraScaleOptionsExtension>();
        var options = existingExtension?.Options.Clone() ?? new TerraScaleEntityFrameworkCoreOptions();
        configure?.Invoke(options);

        var extension = (existingExtension ?? new TerraScaleOptionsExtension()).WithOptions(options);
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);

        return optionsBuilder;
    }

    /// <summary>
    /// Adds TerraScale source metadata to a typed DbContext options builder.
    /// </summary>
    public static DbContextOptionsBuilder<TContext> UseTerraScaleSource<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<TerraScaleEntityFrameworkCoreOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        UseTerraScaleSource((DbContextOptionsBuilder)optionsBuilder, configure);
        return optionsBuilder;
    }
}
