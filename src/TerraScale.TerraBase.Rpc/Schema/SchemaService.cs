using System.Text.Json;
using MagicOnion;
using MagicOnion.Server;
using TerraScale.TerraBase.Contracts.Schema;

namespace TerraScale.TerraBase.Rpc.Schema
{
    /// <summary>Serves the schema contracts through TerraBase’s REST pipeline.</summary>
    public sealed class SchemaService(RestApi api) : ServiceBase<ISchemaService>, ISchemaService
    {
        private static readonly SchemaJson Json = SchemaJson.Default;

        /// <inheritdoc />
        public async UnaryResult<TerraBaseCollectionSchema> GetInferredSchemaAsync(string projectId, string database, string collection)
        {
            var answer = await api.SendAsync(Context, "GET", RestPaths.Database(projectId, database) + "/schema/" + Uri.EscapeDataString(collection));
            return RestApi.Read(answer, Json.TerraBaseCollectionSchema);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseDeclaredSchema> GetDeclaredSchemaAsync(string projectId, string database, string collection)
        {
            var answer = await api.SendAsync(Context, "GET", RestPaths.Collection(projectId, database, collection) + "/schema");
            return RestApi.Read(answer, Json.TerraBaseDeclaredSchema);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseDeclaredSchema> PutDeclaredSchemaAsync(string projectId, string database, string collection, JsonElement schema)
        {
            var answer = await api.SendAsync(Context, "PUT", RestPaths.Collection(projectId, database, collection) + "/schema", RestApi.Json(schema, Json.JsonElement));
            return RestApi.Read(answer, Json.TerraBaseDeclaredSchema);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseDeclaredSchema> DeleteDeclaredSchemaAsync(string projectId, string database, string collection)
        {
            var answer = await api.SendAsync(Context, "DELETE", RestPaths.Collection(projectId, database, collection) + "/schema");
            return RestApi.Read(answer, Json.TerraBaseDeclaredSchema);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseSchemaPreview> PreviewSchemaAsync(string projectId, string database, string collection, JsonElement schema, string? cursor)
        {
            var answer = await api.SendAsync(Context, "POST", RestPaths.Collection(projectId, database, collection) + "/schema/preview" + RestPaths.Query(("cursor", cursor)), RestApi.Json(schema, Json.JsonElement));
            return RestApi.Read(answer, Json.TerraBaseSchemaPreview);
        }
    }
}
