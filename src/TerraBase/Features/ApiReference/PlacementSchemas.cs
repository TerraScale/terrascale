using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using TerraBase.Features.Management;
using TerraScale.TerraBase.Contracts.Management;

namespace TerraBase.Features.ApiReference;

internal static class PlacementSchemas
{
    private static readonly string[] TransitionalFields = ["homeRegion", "replicaRegions", "allRegions", "replicas"];

    public static Task ApplyAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Type != typeof(DatabaseRegionsRequest) && context.JsonTypeInfo.Type != typeof(ManagedDatabaseBody)
            && context.JsonTypeInfo.Type != typeof(TerraBaseDatabaseRegions))
        {
            return Task.CompletedTask;
        }

        if (schema.Properties is not { } properties)
        {
            return Task.CompletedTask;
        }

        foreach (var name in TransitionalFields)
        {
            if (properties.TryGetValue(name, out var legacy) && legacy is OpenApiSchema property)
            {
                property.Description = "Transitional: keeps its existing behavior; removed with move-home by category C23 of batch db4a5ba7 once every consumer sends regions. Cannot be combined with regions in a request.";
            }
        }

        if (properties.TryGetValue("regions", out var selected) && selected is OpenApiSchema regions)
        {
            regions.Type = JsonSchemaType.Array;
            regions.Description = context.JsonTypeInfo.Type == typeof(DatabaseRegionsRequest)
                ? "At least one distinct serving region id. On create only, the first requested entry chooses the initial writer. Send separately from homeRegion, replicaRegions and allRegions."
                : "Selected regions in catalog order, sorted by region id. Order carries no meaning; a status entry identifies the writer with role writer.";
            if (context.JsonTypeInfo.Type == typeof(DatabaseRegionsRequest))
            {
                regions.MinItems = 1;
                regions.UniqueItems = true;
            }
            else
            {
                schema.Required ??= new HashSet<string>(StringComparer.Ordinal);
                schema.Required.Add("regions");
            }
        }

        return Task.CompletedTask;
    }
}
