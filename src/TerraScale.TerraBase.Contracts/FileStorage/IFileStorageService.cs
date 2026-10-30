using MagicOnion;

namespace TerraScale.TerraBase.Contracts.FileStorage
{
    /// <summary>The FileStorage REST API over RPC, with identical authorization and placement.</summary>
    public interface IFileStorageService : IService<IFileStorageService>
    {
        /// <summary>Creates a project bucket.</summary>
        UnaryResult<TerraBaseFileBucket> CreateFileBucketAsync(string projectId, string name, TerraBaseFileRules rules);

        /// <summary>Lists at most 50 project buckets after an exclusive ULID.</summary>
        UnaryResult<TerraBaseFileBucketPage> ListFileBucketsAsync(string projectId, string? after);

        /// <summary>Replaces read and write rules; the owner pointer is immutable.</summary>
        UnaryResult<TerraBaseFileBucket> SetFileBucketRulesAsync(string projectId, string bucketId, TerraBaseFileRules rules);

        /// <summary>Deletes an empty bucket.</summary>
        UnaryResult DeleteFileBucketAsync(string projectId, string bucketId);

        /// <summary>Reserves a 1-byte to 100-MiB file and returns its private upload URL and required headers. Bytes go directly to that URL.</summary>
        UnaryResult<TerraBaseFileUrl> CreateFileUploadAsync(string projectId, string bucketId, TerraBaseFileUpload upload);

        /// <summary>Verifies size and checksum before publishing the file.</summary>
        UnaryResult<TerraBaseStoredFile> CompleteFileUploadAsync(string projectId, string bucketId, string fileId);

        /// <summary>Lists at most 50 readable completed files after an exclusive ULID.</summary>
        UnaryResult<TerraBaseStoredFilePage> ListBucketFilesAsync(string projectId, string bucketId, string? after);

        /// <summary>Returns a private 60-second attachment download URL.</summary>
        UnaryResult<TerraBaseFileUrl> CreateFileDownloadAsync(string projectId, string bucketId, string fileId);

        /// <summary>Revokes new downloads and queues physical cleanup.</summary>
        UnaryResult DeleteBucketFileAsync(string projectId, string bucketId, string fileId);
    }
}
