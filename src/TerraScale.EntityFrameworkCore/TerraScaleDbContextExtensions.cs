using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using TerraScale.DotNet;

namespace TerraScale.EntityFrameworkCore;

/// <summary>
/// DbContext helpers for TerraScale entity sources.
/// </summary>
public static class TerraScaleDbContextExtensions
{
    /// <summary>
    /// Creates a TerraScale source facade for this DbContext.
    /// </summary>
    public static TerraScaleEntitySource<TContext> AsTerraScaleSource<TContext>(
        this TContext context,
        TerraScaleClient client,
        TerraScaleEntityFrameworkCoreOptions? options = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(client);

        return new TerraScaleEntitySource<TContext>(
            context,
            client,
            options ?? GetTerraScaleOptions(context));
    }

    /// <summary>
    /// Creates a TerraScale entity set for this DbContext and entity type.
    /// </summary>
    public static TerraScaleEntitySet<TEntity> TerraScaleSet<TEntity>(
        this DbContext context,
        TerraScaleClient client,
        TerraScaleEntityFrameworkCoreOptions? options = null)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(client);

        return new TerraScaleEntitySet<TEntity>(
            context,
            client,
            options ?? GetTerraScaleOptions(context));
    }

    internal static TerraScaleEntityFrameworkCoreOptions GetTerraScaleOptions(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var extension = context
            .GetService<IDbContextOptions>()
            .FindExtension<TerraScaleOptionsExtension>();

        return extension?.Options.Clone() ?? new TerraScaleEntityFrameworkCoreOptions();
    }
}
