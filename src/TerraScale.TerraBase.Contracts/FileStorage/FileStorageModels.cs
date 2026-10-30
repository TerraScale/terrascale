using System.Text.Json;
using MessagePack;

namespace TerraScale.TerraBase.Contracts.FileStorage
{
    /// <summary>Owner, public and trusted-role grants for one file operation.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseFilePolicy(bool Owner, bool Public, IReadOnlyList<string> Roles);
    /// <summary>Document-style rules evaluated against immutable file metadata. Owner is a JSON Pointer.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseFileRules(string? Owner, TerraBaseFilePolicy Read, TerraBaseFilePolicy Write);
    /// <summary>A project's logical file bucket. Region identifies its object backing.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseFileBucket(string Id, string Name, string Region, TerraBaseFileRules Rules);
    /// <summary>A file's immutable metadata. Pending files cannot be downloaded.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseStoredFile(string Id, string Name, string ContentType, long Size, string Sha256,
        JsonElement Metadata, string Status, DateTimeOffset UploadExpiresAt);
    /// <summary>A private short-lived upload or download capability. Never log Url or Headers.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseFileUrl(string Id, string Url, DateTimeOffset ExpiresAt, IReadOnlyDictionary<string, string> Headers)
    {
        /// <inheritdoc />
        public override string ToString()
        {
            return $"File capability for {Id} (URL redacted)";
        }
    }
    /// <summary>An immutable upload reservation; SHA-256 is base64 and size is 1–104857600 bytes.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseFileUpload(string Name, string ContentType, long Size, string Sha256, JsonElement Metadata);
    /// <summary>The REST body that creates a bucket.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseFileBucketRequest(string Name, TerraBaseFileRules Rules);
    /// <summary>The REST body that replaces grants.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseFileRulesRequest(TerraBaseFileRules Rules);

    /// <summary>One page of project buckets.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseFileBucketPage(IReadOnlyList<TerraBaseFileBucket> Items, string? Next);
    /// <summary>One page of readable completed files.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseStoredFilePage(IReadOnlyList<TerraBaseStoredFile> Items, string? Next);
}
