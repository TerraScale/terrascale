using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.FileStorage;

public sealed record FileStorageListing(TerraBasePage<TerraBaseFileBucket> Buckets, TerraBasePage<TerraBaseStoredFile>? Files, string? BucketId, string? BucketAfter);

public sealed class FileStorageService(AccountService accounts, IProjectAccessTokens tokens, IHttpClientFactory factory)
{
    public async Task<FileStorageListing> ListAsync(AccountContext context, string? bucket, string? after, string? bucketAfter, CancellationToken token)
    {
        var client = await ProjectAsync(context, false, token);
        var buckets = await client.ListFileBucketsAsync(bucketAfter, token);
        if (!string.IsNullOrEmpty(bucket)) TerraScaleIds.Validate(bucket);
        var selected = string.IsNullOrEmpty(bucket) ? buckets.Items.FirstOrDefault()?.Id : bucket;
        var files = selected is null ? null : await client.ListBucketFilesAsync(selected, after, token);
        return new(buckets, files, selected, bucketAfter);
    }

    public async Task CreateBucketAsync(AccountContext context, string name, TerraBaseFileRules rules, CancellationToken token) =>
        _ = await (await ProjectAsync(context, true, token)).CreateFileBucketAsync(name.Trim(), rules, token);

    public async Task SetRulesAsync(AccountContext context, string bucket, TerraBaseFileRules rules, CancellationToken token) =>
        _ = await (await ProjectAsync(context, true, token)).SetFileBucketRulesAsync(bucket, rules, token);

    public async Task DeleteBucketAsync(AccountContext context, string bucket, CancellationToken token) =>
        await (await ProjectAsync(context, true, token)).DeleteFileBucketAsync(bucket, token);

    public async Task<TerraBaseFileUrl> UploadAsync(AccountContext context, string bucket, TerraBaseFileUpload upload, CancellationToken token) =>
        await (await ProjectAsync(context, true, token)).CreateFileUploadAsync(bucket, upload, token);

    public async Task CompleteAsync(AccountContext context, string bucket, string file, CancellationToken token) =>
        _ = await (await ProjectAsync(context, true, token)).CompleteFileUploadAsync(bucket, file, token);

    public async Task DeleteAsync(AccountContext context, string bucket, string file, CancellationToken token) =>
        await (await ProjectAsync(context, true, token)).DeleteBucketFileAsync(bucket, file, token);

    public async Task<TerraBaseFileUrl> DownloadAsync(AccountContext context, string bucket, string file, CancellationToken token) =>
        await (await ProjectAsync(context, false, token)).CreateFileDownloadAsync(bucket, file, token);

    private async Task<TerraBaseProjectClient> ProjectAsync(AccountContext context, bool write, CancellationToken token)
    {
        context = await AccountScope.RefreshAsync(accounts, context, token, requireProject: true);
        if (write && !context.CanWriteProjectData) throw new AccountAccessException("Write access is required.");
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
