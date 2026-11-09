using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace TerraScale.EntityFrameworkCore.Metadata;

internal sealed class TerraBaseConventionSetPlugin : IConventionSetPlugin
{
    public ConventionSet ModifyConventions(ConventionSet conventionSet)
    {
        conventionSet.Add(new TerraBaseUtcDateTimeConvention());
        return conventionSet;
    }
}

internal sealed class TerraBaseUtcDateTimeConvention : IModelFinalizingConvention
{
    private static readonly ValueConverter<DateTime, DateTime> Utc = new(
        value => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value,
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes()) Apply(entityType);
    }

    private static void Apply(IConventionTypeBase type)
    {
        foreach (var property in type.GetDeclaredProperties())
        {
            if ((Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType) == typeof(DateTime)
                && property.GetValueConverterConfigurationSource() is null && property.GetProviderClrType() is null)
            {
                property.Builder.HasConversion(Utc);
            }
        }

        foreach (var complex in type.GetDeclaredComplexProperties()) Apply(complex.ComplexType);
    }
}
