using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using TerraScale.DotNet;

namespace TerraScale.EntityFrameworkCore;

/// <summary>DbContext helpers for TerraScale.</summary>
public static class TerraScaleDbContextExtensions
{
    /// <summary>Creates a TerraScale source facade.</summary>
    public static TerraScaleEntitySource<TContext> AsTerraScaleSource<TContext>(this TContext context, TerraScaleClient client, TerraScaleEntityFrameworkCoreOptions? options = null) where TContext : DbContext => new(context ?? throw new ArgumentNullException(nameof(context)), client ?? throw new ArgumentNullException(nameof(client)), options ?? GetTerraScaleOptions(context));
    /// <summary>Creates an entity set.</summary>
    public static TerraScaleEntitySet<TEntity> TerraScaleSet<TEntity>(this DbContext context, TerraScaleClient client, TerraScaleEntityFrameworkCoreOptions? options = null) where TEntity : class => new(context ?? throw new ArgumentNullException(nameof(context)), client ?? throw new ArgumentNullException(nameof(client)), options ?? GetTerraScaleOptions(context));
    internal static TerraScaleEntityFrameworkCoreOptions GetTerraScaleOptions(DbContext context) => context.GetService<IDbContextOptions>().FindExtension<TerraScaleOptionsExtension>()?.Options.Clone() ?? new TerraScaleEntityFrameworkCoreOptions();
}
