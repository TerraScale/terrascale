using MagicOnion;
using MagicOnion.Server;
using TerraScale.TerraBase.Contracts.Import;

namespace TerraScale.TerraBase.Rpc.Import
{
    /// <summary>Serves <see cref="IImportService"/> through TerraBase’s REST pipeline.</summary>
    public sealed class ImportService(RestApi api) : ServiceBase<IImportService>, IImportService
    {
        private static readonly ImportJson Json = ImportJson.Default;

        /// <inheritdoc />
        public async UnaryResult<TerraBaseImportJob> CreateImportAsync(string projectId, string database, TerraBaseImportFormat format, long sourceBytes, string sourceSha256, TerraBaseImportOptions? options)
        {
            return RestApi.Read(await api.SendAsync(Context, "POST", Path(projectId, database), RestApi.Json(new TerraBaseCreateImport(format, sourceBytes, sourceSha256, options), Json.TerraBaseCreateImport)), Json.TerraBaseImportJob);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseImportJob> GetImportAsync(string projectId, string database)
        {
            return RestApi.Read(await api.SendAsync(Context, "GET", Path(projectId, database)), Json.TerraBaseImportJob);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseImportJob> UploadImportSourceAsync(string projectId, string database, long offset, byte[] part)
        {
            return RestApi.Read(await api.SendAsync(Context, "PUT", Path(projectId, database) + "/source" + RestPaths.Query(("offset", RestPaths.Number(offset))), part, "application/octet-stream"), Json.TerraBaseImportJob);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseImportJob> UpdateImportAsync(string projectId, string database, TerraBaseImportOptions options)
        {
            return RestApi.Read(await api.SendAsync(Context, "PATCH", Path(projectId, database), RestApi.Json(new TerraBaseUpdateImport(options), Json.TerraBaseUpdateImport)), Json.TerraBaseImportJob);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseImportJob> StartImportAsync(string projectId, string database, string previewId)
        {
            return RestApi.Read(await api.SendAsync(Context, "POST", Path(projectId, database) + "/start", RestApi.Json(new TerraBaseStartImport(previewId), Json.TerraBaseStartImport)), Json.TerraBaseImportJob);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseImportJob> ResumeImportAsync(string projectId, string database)
        {
            return RestApi.Read(await api.SendAsync(Context, "POST", Path(projectId, database) + "/resume"), Json.TerraBaseImportJob);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseImportJob> CancelImportAsync(string projectId, string database)
        {
            return RestApi.Read(await api.SendAsync(Context, "POST", Path(projectId, database) + "/cancel"), Json.TerraBaseImportJob);
        }

        private static string Path(string projectId, string database)
        {
            return RestPaths.Database(projectId, database) + "/import";
        }
    }
}
