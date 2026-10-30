using MagicOnion;

namespace TerraScale.TerraBase.Contracts.Import
{
    /// <summary>The Import REST API over RPC, with identical authorization and placement.</summary>
    public interface IImportService : IService<IImportService>
    {
        /// <summary>Creates the import in a fresh empty destination; source size is 1 byte to 1 GiB.</summary>
        UnaryResult<TerraBaseImportJob> CreateImportAsync(string projectId, string database, TerraBaseImportFormat format, long sourceBytes, string sourceSha256, TerraBaseImportOptions? options);

        /// <summary>Reads the destination’s import.</summary>
        UnaryResult<TerraBaseImportJob> GetImportAsync(string projectId, string database);

        /// <summary>Uploads one 1-byte to 4-MiB part at receivedBytes; a fully received part is a replay. Parts preserve REST offsets, checksum validation and cancellation.</summary>
        UnaryResult<TerraBaseImportJob> UploadImportSourceAsync(string projectId, string database, long offset, byte[] part);

        /// <summary>Replaces options and analyzes the source again.</summary>
        UnaryResult<TerraBaseImportJob> UpdateImportAsync(string projectId, string database, TerraBaseImportOptions options);

        /// <summary>Starts the exact confirmed preview.</summary>
        UnaryResult<TerraBaseImportJob> StartImportAsync(string projectId, string database, string previewId);

        /// <summary>Resumes a cancelled or failed import under its confirmed preview.</summary>
        UnaryResult<TerraBaseImportJob> ResumeImportAsync(string projectId, string database);

        /// <summary>Cancels future work while retaining documents already written.</summary>
        UnaryResult<TerraBaseImportJob> CancelImportAsync(string projectId, string database);
    }
}
