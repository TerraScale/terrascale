using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using TerraBase.Backup;

namespace TerraBase.Benchmarks;

/// <summary>Run-owned S3 fixture; credentials are read only from the process environment.</summary>
internal sealed class RustFsWorkloadStore : IDisposable
{
    private readonly AmazonS3Client client;
    private RustFsWorkloadStore(AmazonS3Client client, IBackupObjectStore store, string bucket)
    {
        this.client = client;
        Store = store;
        Bucket = bucket;
    }
    internal IBackupObjectStore Store { get; }
    internal string Bucket { get; }
    internal static async Task<RustFsWorkloadStore> OpenAsync(string checkpointRoot)
    {
        static string Required(string name) => Environment.GetEnvironmentVariable(name)
            ?? throw new ArgumentException($"The RustFS lane requires environment variable {name}.");
        var bucket = "terrabase-e2e-" + Guid.NewGuid().ToString("N");
        var client = new AmazonS3Client(new BasicAWSCredentials(Required("TERRABASE_S3_ACCESS_KEY"), Required("TERRABASE_S3_SECRET_KEY")),
            new AmazonS3Config
            {
                ServiceURL = Required("TERRABASE_S3_ENDPOINT"),
                ForcePathStyle = true,
                AuthenticationRegion = Environment.GetEnvironmentVariable("TERRABASE_S3_REGION") ?? "us-east-1",
            });
        try
        {
            await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }).ConfigureAwait(false);
            return new(client, new S3NativeBackupObjectStore(client, bucket,
                checkpointStore: new FileBackupUploadCheckpointStore(checkpointRoot)), bucket);
        }
        catch { client.Dispose(); throw; }
    }
    public void Dispose() => client.Dispose();
}
