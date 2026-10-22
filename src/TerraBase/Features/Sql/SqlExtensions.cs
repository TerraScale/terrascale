namespace TerraBase.Features.Sql;

/// <summary>Registers the relational SQL slice.</summary>
public static class SqlExtensions
{
    /// <summary>Registers <see cref="ISqlService"/>.</summary>
    public static IServiceCollection AddTerraBaseSql(this IServiceCollection services) => services.AddSingleton<ISqlService, SqlService>();
}
