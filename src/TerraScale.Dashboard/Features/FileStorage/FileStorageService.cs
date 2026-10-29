using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.FileStorage
{
    public sealed record FileStorageListing(TerraBasePage<TerraBaseFileBucket> Buckets, TerraBasePage<TerraBaseStoredFile>? Files, string? BucketId, string? BucketAfter);

    public sealed class FileStorageService(AccountService accounts, IProjectAccessTokens tokens, IHttpClientFactory factory)
    {
        public async Task<FileStorageListing> ListAsync(AccountContext context, string? bucket, string? after, string? bucketAfter, CancellationToken token)
        {
            var client = await ProjectAsync(context, false, token);
            if (!string.IsNullOrEmpty(bucket))
            {
                TerraScaleIds.Validate(bucket);
            }

            TerraBasePage<TerraBaseFileBucket> buckets;
            try { buckets = await client.ListFileBucketsAsync(bucketAfter, token); }
            // TerraBase enrolls a project with its first database or bucket; before that it has nothing to list, and the
            // page must offer bucket creation (which enrolls the project) instead of an error.
            catch (TerraBaseNotFoundException error) when (error.ErrorCode == "unknown_database" && bucketAfter is null && string.IsNullOrEmpty(bucket))
            { return new(new([], null), null, null, null); }
            var selected = string.IsNullOrEmpty(bucket) ? (buckets.Items is [var firstBucket, ..] ? firstBucket?.Id : null) : bucket;
            var files = selected is null ? null : await client.ListBucketFilesAsync(selected, after, token);
            return new(buckets, files, selected, bucketAfter);
        }

        public async Task CreateBucketAsync(AccountContext context, string name, TerraBaseFileRules rules, CancellationToken token)
        {
            _ = await (await ProjectAsync(context, true, token)).CreateFileBucketAsync(name.Trim(), rules, token);
        }

        public async Task SetRulesAsync(AccountContext context, string bucket, TerraBaseFileRules rules, CancellationToken token)
        {
            _ = await (await ProjectAsync(context, true, token)).SetFileBucketRulesAsync(bucket, rules, token);
        }

        public async Task DeleteBucketAsync(AccountContext context, string bucket, string confirmation, CancellationToken token)
        {
            var client = await ProjectAsync(context, true, token);
            TerraScaleIds.Validate(bucket);
            string? after = null;
            do
            {
                var page = await client.ListFileBucketsAsync(after, token);
                if (page.Items.FirstOrDefault(item => item.Id == bucket) is { } target)
                {
                    if (!string.Equals(confirmation, target.Name, StringComparison.Ordinal))
                    {
                        throw new AccountAccessException("Type the bucket name exactly to confirm deletion.", 422);
                    }

                    await client.DeleteFileBucketAsync(bucket, token);
                    return;
                }
                after = page.Next;
            } while (after is not null);
            throw new AccountAccessException("The bucket is unavailable in this project.", 404);
        }

        public async Task<TerraBaseFileUrl> UploadAsync(AccountContext context, string bucket, TerraBaseFileUpload upload, CancellationToken token)
        {
            return await (await ProjectAsync(context, true, token)).CreateFileUploadAsync(bucket, upload, token);
        }

        public async Task CompleteAsync(AccountContext context, string bucket, string file, CancellationToken token)
        {
            _ = await (await ProjectAsync(context, true, token)).CompleteFileUploadAsync(bucket, file, token);
        }

        public async Task DeleteAsync(AccountContext context, string bucket, string file, CancellationToken token)
        {
            await (await ProjectAsync(context, true, token)).DeleteBucketFileAsync(bucket, file, token);
        }

        public async Task<TerraBaseFileUrl> DownloadAsync(AccountContext context, string bucket, string file, CancellationToken token)
        {
            var signed = await (await ProjectAsync(context, false, token)).CreateFileDownloadAsync(bucket, file, token);
            if (!Uri.TryCreate(signed.Url, UriKind.Absolute, out var url) || url.Scheme != "https" || url.UserInfo.Length > 0
                || url.Fragment.Length > 0 || !url.Host.EndsWith(".r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException("Invalid download capability.");
            }

            return signed;
        }

        private async Task<TerraBaseProjectClient> ProjectAsync(AccountContext context, bool write, CancellationToken token)
        {
            context = await AccountScope.RefreshAsync(accounts, context, token, requireProject: true);
            if (write && !context.CanWriteProjectData)
            {
                throw new AccountAccessException("Write access is required.");
            }

            var project = context.Project!;
            var accessToken = await tokens.GetAsync(context.Session, context.Organization!.Id, project.Id,
                write ? ["documents:read", "documents:write"] : ["documents:read"], token);
            var origin = await new TerraBaseDiscovery().ResolveAsync(token);
            return new TerraBaseDocumentsClient(factory.CreateClient("platform"), new()
            {
                BaseAddress = origin,
                AccessTokenProvider = _ => ValueTask.FromResult(accessToken)
            }).Project(project.Id);
        }
    }
}
