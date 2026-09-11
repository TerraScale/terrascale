using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace TerraScale.EntityFrameworkCore;

/// <summary>DbContext options helpers for TerraScale.</summary>
public static class TerraScaleDbContextOptionsBuilderExtensions
{
    /// <summary>Registers TerraScale metadata on a DbContext options builder.</summary>
    public static DbContextOptionsBuilder UseTerraScaleSource(this DbContextOptionsBuilder builder, Action<TerraScaleEntityFrameworkCoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var existing = builder.Options.FindExtension<TerraScaleOptionsExtension>();
        var options = existing?.Options.Clone() ?? new TerraScaleEntityFrameworkCoreOptions();
        configure?.Invoke(options);
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(new TerraScaleOptionsExtension(options));
        return builder;
    }

    /// <summary>Registers TerraScale metadata on a typed DbContext options builder.</summary>
    public static DbContextOptionsBuilder<TContext> UseTerraScaleSource<TContext>(this DbContextOptionsBuilder<TContext> builder, Action<TerraScaleEntityFrameworkCoreOptions>? configure = null) where TContext : DbContext
    {
        UseTerraScaleSource((DbContextOptionsBuilder)builder, configure);
        return builder;
    }
}
