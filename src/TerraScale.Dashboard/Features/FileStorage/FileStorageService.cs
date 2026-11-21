using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.Rpc;
using TerraScale.TerraBase.Contracts.FileStorage;
using TerraScale.TerraBase.Contracts;

namespace TerraScale.Dashboard.Features.FileStorage
{
    public sealed record FileStorageListing(TerraBaseFileBucketPage Buckets, TerraBaseStoredFilePage? Files, string? BucketId, string? BucketAfter);
    public sealed record FileClient(IFileStorageService Client, string ProjectId);
    public readonly union ListFilesResult(FileStorageListing, Validation);
    public readonly union CreateBucketResult(TerraBaseFileBucket, Forbidden);
    public readonly union SetRulesResult(Done, Forbidden);
    public readonly union DeleteBucketResult(Done, Validation, Forbidden);
    public readonly union UploadFileResult(TerraBaseFileUrl, Forbidden);
    public readonly union CompleteUploadResult(Done, Forbidden);
    public readonly union DeleteFileResult(Done, Forbidden);

    public sealed class FileStorageService(AccountService accounts, IProjectAccessTokens tokens, TerraBaseRpcClient rpc)
    {
        public async Task<ListFilesResult> ListAsync(AccountContext context, string? bucket, string? after, string? bucketAfter, CancellationToken token)
        {
            var client = await ReadClientAsync(context, token);
            if (!string.IsNullOrEmpty(bucket) && !TerraScaleIds.IsValid(bucket))
            {
                return new Validation("Bucket IDs are 26-character uppercase ULIDs.");
            }

            TerraBaseFileBucketPage buckets;
            try { buckets = await client.Client.ListFileBucketsAsync(client.ProjectId, bucketAfter); }
            // TerraBase enrolls a project with its first database or bucket; before that it has nothing to list, and the
            // page must offer bucket creation (which enrolls the project) instead of an error.
            catch (TerraBaseNotFoundException error) when (error.ErrorCode == "unknown_database" && bucketAfter is null && string.IsNullOrEmpty(bucket))
            { return new FileStorageListing(new([], null), null, null, null); }
            var selected = string.IsNullOrEmpty(bucket) ? (buckets.Items is [var firstBucket, ..] ? firstBucket?.Id : null) : bucket;
            var files = selected is null ? null : await client.Client.ListBucketFilesAsync(client.ProjectId, selected, after);
            return new FileStorageListing(buckets, files, selected, bucketAfter);
        }

        public async Task<FileStorageListing> ListBucketsAsync(AccountContext context, string? bucketAfter, CancellationToken token)
        {
            var client = await ReadClientAsync(context, token);
            try { return new(await client.Client.ListFileBucketsAsync(client.ProjectId, bucketAfter), null, null, bucketAfter); }
            catch (TerraBaseNotFoundException error) when (error.ErrorCode == "unknown_database" && bucketAfter is null)
            { return new(new([], null), null, null, null); }
        }

        public async Task<CreateBucketResult> CreateBucketAsync(AccountContext context, string name, TerraBaseFileRules rules, CancellationToken token)
        {
            return await WriteClientAsync(context, token) is not { } write
                ? new Forbidden("Write access is required.")
                : await write.Client.CreateFileBucketAsync(write.ProjectId, name.Trim(), rules);
        }

        public async Task<SetRulesResult> SetRulesAsync(AccountContext context, string bucket, TerraBaseFileRules rules, CancellationToken token)
        {
            if (await WriteClientAsync(context, token) is not { } write)
            {
                return new Forbidden("Write access is required.");
            }

            _ = await write.Client.SetFileBucketRulesAsync(write.ProjectId, bucket, rules);
            return new Done();
        }

        public async Task<DeleteBucketResult> DeleteBucketAsync(AccountContext context, string bucket, CancellationToken token)
        {
            if (await WriteClientAsync(context, token) is not { } write)
            {
                return new Forbidden("Write access is required.");
            }

            if (!TerraScaleIds.IsValid(bucket))
            {
                return new Validation("Bucket IDs are 26-character uppercase ULIDs.");
            }

            await write.Client.DeleteFileBucketAsync(write.ProjectId, bucket);
            return new Done();
        }

        public async Task<UploadFileResult> UploadAsync(AccountContext context, string bucket, TerraBaseFileUpload upload, CancellationToken token)
        {
            if (await WriteClientAsync(context, token) is not { } write)
            {
                return new Forbidden("Write access is required.");
            }

            ArgumentNullException.ThrowIfNull(upload);
            if (upload.Size is < 1 or > 100 * 1024 * 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(upload), "File size must be 1–104857600 bytes.");
            }

            if (upload.Metadata.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                throw new ArgumentException("File metadata must be a JSON object.", nameof(upload));
            }

            return await write.Client.CreateFileUploadAsync(write.ProjectId, bucket, upload);
        }

        public async Task<CompleteUploadResult> CompleteAsync(AccountContext context, string bucket, string file, CancellationToken token)
        {
            if (await WriteClientAsync(context, token) is not { } write)
            {
                return new Forbidden("Write access is required.");
            }

            _ = await write.Client.CompleteFileUploadAsync(write.ProjectId, bucket, file);
            return new Done();
        }

        public async Task<DeleteFileResult> DeleteAsync(AccountContext context, string bucket, string file, CancellationToken token)
        {
            if (await WriteClientAsync(context, token) is not { } write)
            {
                return new Forbidden("Write access is required.");
            }

            await write.Client.DeleteBucketFileAsync(write.ProjectId, bucket, file);
            return new Done();
        }

        public async Task<TerraBaseFileUrl> DownloadAsync(AccountContext context, string bucket, string file, CancellationToken token)
        {
            var client = await ReadClientAsync(context, token);
            var signed = await client.Client.CreateFileDownloadAsync(client.ProjectId, bucket, file);
            if (!Uri.TryCreate(signed.Url, UriKind.Absolute, out var url) || url.Scheme != "https" || url.UserInfo.Length > 0
                || url.Fragment.Length > 0 || !url.Host.EndsWith(".r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException("Invalid download capability.");
            }

            return signed;
        }

        private async Task<FileClient> ReadClientAsync(AccountContext context, CancellationToken token)
        {
            return (await ClientAsync(context, false, token))!;
        }

        private async Task<FileClient?> WriteClientAsync(AccountContext context, CancellationToken token)
        {
            return await ClientAsync(context, true, token);
        }

        private async Task<FileClient?> ClientAsync(AccountContext context, bool write, CancellationToken token)
        {
            context = await AccountScope.RefreshAsync(accounts, context, token, requireProject: true);
            if (write && !context.CanWriteProjectData)
            {
                return null;
            }

            var project = context.Project!;
            var accessToken = await tokens.GetAsync(context.Session, context.Organization!.Id, project.Id,
                write ? ["sql:read", "sql:write"] : ["sql:read"], token);
            return new(rpc.User<IFileStorageService>(accessToken).WithCancellationToken(token), project.Id);
        }
    }
}
