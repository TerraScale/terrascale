using System.ComponentModel;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.FileStorage
{
    [McpServerToolType]
    public sealed class FileStorageMcpTools(IHttpContextAccessor http, FileStorageService files)
    {
        [McpServerTool(Name = "list_file_storage", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List project buckets and completed files in the selected bucket (or first bucket). Pass buckets.next as bucketAfter and files.next as after to continue each listing. Requires document read access.")]
        public Task<FileStorageListing> List(string? bucketId = null, string? after = null, string? bucketAfter = null, string? organizationId = null, string? projectId = null)
        {
            return Request(() => files.ListAsync(Scope(organizationId, projectId), bucketId, after, bucketAfter, Cancellation));
        }

        [McpServerTool(Name = "create_file_bucket", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Create a project file bucket with owner, public and trusted-role rules. Owner is a JSON Pointer into immutable file metadata. Requires project data write access.")]
        public Task<McpFileStorageSaved> CreateBucket(string name, TerraBaseFileRules rules, string? organizationId = null, string? projectId = null)
        {
            return Save(() => files.CreateBucketAsync(Scope(organizationId, projectId), name, rules, Cancellation));
        }

        [McpServerTool(Name = "set_file_bucket_rules", Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Replace a bucket's read and write rules. The owner pointer is immutable. Public read permits anonymous access; public write permits anonymous writes. Requires project data write access.")]
        public Task<McpFileStorageSaved> SetRules(string bucketId, TerraBaseFileRules rules, string? organizationId = null, string? projectId = null)
        {
            return Save(() => files.SetRulesAsync(Scope(organizationId, projectId), bucketId, rules, Cancellation));
        }

        [McpServerTool(Name = "delete_file_bucket", Destructive = true, OpenWorld = false, UseStructuredContent = true), Description("Delete an empty bucket after explicit user consent. confirmName must equal the bucket name exactly; pending file cleanup may prevent deletion. Requires project data write access.")]
        public Task<McpFileStorageSaved> DeleteBucket(string bucketId, string confirmName, string? organizationId = null, string? projectId = null)
        {
            var context = Scope(organizationId, projectId);
            return Save(() => files.DeleteBucketAsync(context, bucketId, confirmName, Cancellation));
        }

        [McpServerTool(Name = "create_file_upload", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Reserve file storage and return a private five-minute upload capability. Upload size is 1–104857600 bytes, sha256 is base64, and metadata is a JSON object. PUT the exact bytes with every returned header, then call complete_file_upload. Never log or share the URL or headers. Requires project data write access.")]
        public Task<TerraBaseFileUrl> Upload(string bucketId, TerraBaseFileUpload upload, string? organizationId = null, string? projectId = null)
        {
            return Request(() => files.UploadAsync(Scope(organizationId, projectId), bucketId, upload, Cancellation));
        }

        [McpServerTool(Name = "complete_file_upload", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Verify uploaded size and SHA-256 and publish a completed file. Requires project data write access and the file's write rule.")]
        public Task<McpFileStorageSaved> Complete(string bucketId, string fileId, string? organizationId = null, string? projectId = null)
        {
            return Save(() => files.CompleteAsync(Scope(organizationId, projectId), bucketId, fileId, Cancellation));
        }

        [McpServerTool(Name = "delete_file", Destructive = true, OpenWorld = false, UseStructuredContent = true), Description("Revoke new downloads and queue physical file cleanup after explicit user consent. confirmFileId must equal fileId. Existing signed downloads remain valid until expiry. Requires project data write access and the file's write rule.")]
        public Task<McpFileStorageSaved> Delete(string bucketId, string fileId, string confirmFileId, string? organizationId = null, string? projectId = null)
        {
            var context = Scope(organizationId, projectId);
            Confirm(fileId, confirmFileId, "file");
            return Save(() => files.DeleteAsync(context, bucketId, fileId, Cancellation));
        }

        [McpServerTool(Name = "create_file_download", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Return a private short-lived attachment download capability for a completed file. Never log or share the URL or headers. Requires document read access and the file's read rule.")]
        public Task<TerraBaseFileUrl> Download(string bucketId, string fileId, string? organizationId = null, string? projectId = null)
        {
            return Request(() => files.DownloadAsync(Scope(organizationId, projectId), bucketId, fileId, Cancellation));
        }

        private static void Confirm(string id, string confirmation, string kind)
        {
            if (id.Length == 0 || !string.Equals(id, confirmation, StringComparison.Ordinal))
            {
                throw new AccountAccessException("Confirm the " + kind + " ID before deleting it.", 422);
            }
        }
        private static Task<McpFileStorageSaved> Save(Func<Task> operation)
        {
            return Request(async () => { await operation(); return new McpFileStorageSaved(true); });
        }

        private static async Task<T> Request<T>(Func<Task<T>> operation)
        {
            try { return await operation(); }
            catch (ArgumentException) { throw new AccountAccessException("Check the bucket name, access rules, file metadata and upload details.", 422); }
        }
        private AccountContext Scope(string? organizationId, string? projectId)
        {
            return DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
        }

        private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;
    }

    public sealed record McpFileStorageSaved(bool Saved);
}
