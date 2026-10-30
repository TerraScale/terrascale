using MagicOnion;
using MagicOnion.Server;
using TerraScale.TerraBase.Contracts.FileStorage;

namespace TerraScale.TerraBase.Rpc.FileStorage
{
    /// <summary>Serves <see cref="IFileStorageService"/> through TerraBase’s REST pipeline.</summary>
    public sealed class FileStorageService(RestApi api) : ServiceBase<IFileStorageService>, IFileStorageService
    {
        private static readonly FileStorageJson Json = FileStorageJson.Default;

        /// <inheritdoc />
        public async UnaryResult<TerraBaseFileBucket> CreateFileBucketAsync(string projectId, string name, TerraBaseFileRules rules)
        {
            return RestApi.Read(await api.SendAsync(Context, "POST", Buckets(projectId), RestApi.Json(new TerraBaseFileBucketRequest(name, rules), Json.TerraBaseFileBucketRequest)), Json.TerraBaseFileBucket);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseFileBucketPage> ListFileBucketsAsync(string projectId, string? after)
        {
            return RestApi.Read(await api.SendAsync(Context, "GET", Buckets(projectId) + RestPaths.Query(("after", after))), Json.TerraBaseFileBucketPage);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseFileBucket> SetFileBucketRulesAsync(string projectId, string bucketId, TerraBaseFileRules rules)
        {
            return RestApi.Read(await api.SendAsync(Context, "PUT", Bucket(projectId, bucketId) + "/rules", RestApi.Json(new TerraBaseFileRulesRequest(rules), Json.TerraBaseFileRulesRequest)), Json.TerraBaseFileBucket);
        }

        /// <inheritdoc />
        public async UnaryResult DeleteFileBucketAsync(string projectId, string bucketId)
        {
            RestApi.Ensure(await api.SendAsync(Context, "DELETE", Bucket(projectId, bucketId)));
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseFileUrl> CreateFileUploadAsync(string projectId, string bucketId, TerraBaseFileUpload upload)
        {
            return RestApi.Read(await api.SendAsync(Context, "POST", Bucket(projectId, bucketId) + "/files/upload", RestApi.Json(upload, Json.TerraBaseFileUpload)), Json.TerraBaseFileUrl);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseStoredFile> CompleteFileUploadAsync(string projectId, string bucketId, string fileId)
        {
            return RestApi.Read(await api.SendAsync(Context, "POST", File(projectId, bucketId, fileId) + "/complete"), Json.TerraBaseStoredFile);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseStoredFilePage> ListBucketFilesAsync(string projectId, string bucketId, string? after)
        {
            return RestApi.Read(await api.SendAsync(Context, "GET", Bucket(projectId, bucketId) + "/files" + RestPaths.Query(("after", after))), Json.TerraBaseStoredFilePage);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseFileUrl> CreateFileDownloadAsync(string projectId, string bucketId, string fileId)
        {
            return RestApi.Read(await api.SendAsync(Context, "POST", File(projectId, bucketId, fileId) + "/download"), Json.TerraBaseFileUrl);
        }

        /// <inheritdoc />
        public async UnaryResult DeleteBucketFileAsync(string projectId, string bucketId, string fileId)
        {
            RestApi.Ensure(await api.SendAsync(Context, "DELETE", File(projectId, bucketId, fileId)));
        }

        private static string Buckets(string projectId)
        {
            return RestPaths.Project(projectId) + "/buckets";
        }

        private static string Bucket(string projectId, string bucketId)
        {
            return Buckets(projectId) + "/" + Uri.EscapeDataString(bucketId);
        }

        private static string File(string projectId, string bucketId, string fileId)
        {
            return Bucket(projectId, bucketId) + "/files/" + Uri.EscapeDataString(fileId);
        }
    }
}
