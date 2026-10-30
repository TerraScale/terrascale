using System.Text.Json;
using MagicOnion;

namespace TerraScale.TerraBase.Contracts.Schema
{
    /// <summary>The schema API, authorized and metered through the corresponding REST routes.</summary>
    public interface ISchemaService : IService<ISchemaService>
    {
        /// <summary><c>GET …/schema/{collection}</c>: inferred fields and unique keys.</summary>
        UnaryResult<TerraBaseCollectionSchema> GetInferredSchemaAsync(string projectId, string database, string collection);

        /// <summary><c>GET …/collections/{collection}/schema</c>.</summary>
        UnaryResult<TerraBaseDeclaredSchema> GetDeclaredSchemaAsync(string projectId, string database, string collection);

        /// <summary><c>PUT …/collections/{collection}/schema</c>.</summary>
        UnaryResult<TerraBaseDeclaredSchema> PutDeclaredSchemaAsync(string projectId, string database, string collection, JsonElement schema);

        /// <summary><c>DELETE …/collections/{collection}/schema</c>.</summary>
        UnaryResult<TerraBaseDeclaredSchema> DeleteDeclaredSchemaAsync(string projectId, string database, string collection);

        /// <summary><c>POST …/collections/{collection}/schema/preview</c>.</summary>
        UnaryResult<TerraBaseSchemaPreview> PreviewSchemaAsync(string projectId, string database, string collection, JsonElement schema, string? cursor);
    }
}
