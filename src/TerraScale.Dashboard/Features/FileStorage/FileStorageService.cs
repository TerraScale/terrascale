using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.Rpc;
using TerraScale.TerraBase.Contracts.FileStorage;
using TerraScale.TerraBase.Contracts;

namespace TerraScale.Dashboard.Features.FileStorage
{
    public sealed record FileStorageListing(TerraBaseFileBucketPage Buckets, TerraBaseStoredFilePage? Files, string? BucketId, string? BucketAfter);

    public sealed class FileStorageService(AccountService accounts, IProjectAccessTokens tokens, TerraBaseRpcClient rpc)
    {
        public async Task<FileStorageListing> ListAsync(AccountContext context, string? bucket, string? after, string? bucketAfter, CancellationToken token)
        {
            var (client, project) = await ProjectAsync(context, false, token);
            if (!string.IsNullOrEmpty(bucket))
            {
                DataService.ValidDocumentId(bucket);
            }

            TerraBaseFileBucketPage buckets;
            try { buckets = await client.ListFileBucketsAsync(project, bucketAfter); }
            // TerraBase enrolls a project with its first database or bucket; before that it has nothing to list, and the
            // page must offer bucket creation (which enrolls the project) instead of an error.
            catch (TerraBaseNotFoundException error) when (error.ErrorCode == "unknown_database" && bucketAfter is null && string.IsNullOrEmpty(bucket))
            { return new(new([], null), null, null, null); }
            var selected = string.IsNullOrEmpty(bucket) ? (buckets.Items is [var firstBucket, ..] ? firstBucket?.Id : null) : bucket;
            var files = selected is null ? null : await client.ListBucketFilesAsync(project, selected, after);
            return new(buckets, files, selected, bucketAfter);
        }

        public async Task CreateBucketAsync(AccountContext context, string name, TerraBaseFileRules rules, CancellationToken token)
        {
            var (client, project) = await ProjectAsync(context, true, token);
            _ = await client.CreateFileBucketAsync(project, name.Trim(), rules);
        }

        public async Task SetRulesAsync(AccountContext context, string bucket, TerraBaseFileRules rules, CancellationToken token)
        {
            var (client, project) = await ProjectAsync(context, true, token);
            _ = await client.SetFileBucketRulesAsync(project, bucket, rules);
        }

        public async Task DeleteBucketAsync(AccountContext context, string bucket, string confirmation, CancellationToken token)
        {
            var (client, project) = await ProjectAsync(context, true, token);
            DataService.ValidDocumentId(bucket);
            string? after = null;
            do
            {
                var page = await client.ListFileBucketsAsync(project, after);
                if (page.Items.FirstOrDefault(item => item.Id == bucket) is { } target)
                {
                    if (!string.Equals(confirmation, target.Name, StringComparison.Ordinal))
                    {
                        throw new AccountAccessException("Type the bucket name exactly to confirm deletion.", 422);
                    }

                    await client.DeleteFileBucketAsync(project, bucket);
                    return;
                }
                after = page.Next;
            } while (after is not null);
            throw new AccountAccessException("The bucket is unavailable in this project.", 404);
        }

        public async Task<TerraBaseFileUrl> UploadAsync(AccountContext context, string bucket, TerraBaseFileUpload upload, CancellationToken token)
        {
            var (client, project) = await ProjectAsync(context, true, token);
            ArgumentNullException.ThrowIfNull(upload);
            if (upload.Size is < 1 or > 100 * 1024 * 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(upload), "File size must be 1–104857600 bytes.");
            }

            if (upload.Metadata.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                throw new ArgumentException("File metadata must be a JSON object.", nameof(upload));
            }

            return await client.CreateFileUploadAsync(project, bucket, upload);
        }

        public async Task CompleteAsync(AccountContext context, string bucket, string file, CancellationToken token)
        {
            var (client, project) = await ProjectAsync(context, true, token);
            _ = await client.CompleteFileUploadAsync(project, bucket, file);
        }

        public async Task DeleteAsync(AccountContext context, string bucket, string file, CancellationToken token)
        {
            var (client, project) = await ProjectAsync(context, true, token);
            await client.DeleteBucketFileAsync(project, bucket, file);
        }

        public async Task<TerraBaseFileUrl> DownloadAsync(AccountContext context, string bucket, string file, CancellationToken token)
        {
            var (client, project) = await ProjectAsync(context, false, token);
            var signed = await client.CreateFileDownloadAsync(project, bucket, file);
            if (!Uri.TryCreate(signed.Url, UriKind.Absolute, out var url) || url.Scheme != "https" || url.UserInfo.Length > 0
                || url.Fragment.Length > 0 || !url.Host.EndsWith(".r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException("Invalid download capability.");
            }

            return signed;
        }

        private async Task<(IFileStorageService Client, string ProjectId)> ProjectAsync(AccountContext context, bool write, CancellationToken token)
        {
            context = await AccountScope.RefreshAsync(accounts, context, token, requireProject: true);
            if (write && !context.CanWriteProjectData)
            {
                throw new AccountAccessException("Write access is required.");
            }

            var project = context.Project!;
            var accessToken = await tokens.GetAsync(context.Session, context.Organization!.Id, project.Id,
                write ? ["documents:read", "documents:write"] : ["documents:read"], token);
            return (rpc.User<IFileStorageService>(accessToken).WithCancellationToken(token), project.Id);
        }
    }
}
