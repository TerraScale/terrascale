using System.Text.Json;

namespace TerraScale.DotNet;

/// <summary>Provider-independent tenant file storage operations.</summary>
public sealed class TerraScaleFilesClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TerraScaleClient client;

    internal TerraScaleFilesClient(TerraScaleClient client) => this.client = client;

    /// <summary>Uploads one stream as a tenant-owned immutable file.</summary>
    public async Task<FileObject> UploadAsync(
        string database,
        Stream content,
        string? contentType = null,
        string? fileName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("The file stream must be readable.", nameof(content));
        }

        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (buffer.Length > 64L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(content), "File content exceeds the 64 MiB limit.");
        }

        var query = fileName is null ? string.Empty : $"?fileName={Uri.EscapeDataString(fileName)}";
        var bytes = await client.SendRawBytesAsync(
            HttpMethod.Post,
            $"/api/v1/databases/{Escape(database)}/files{query}",
            buffer.ToArray(),
            contentType ?? "application/octet-stream",
            cancellationToken).ConfigureAwait(false);
        return Deserialize<FileObject>(bytes);
    }

    /// <summary>Lists file metadata owned by one database tenant.</summary>
    public async Task<IReadOnlyList<FileObject>> ListAsync(
        string database,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        var bytes = await client.SendRawBytesAsync(
            HttpMethod.Get,
            $"/api/v1/databases/{Escape(database)}/files",
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        return Deserialize<FileObjectsResponse>(bytes).Files;
    }

    /// <summary>Reads immutable metadata for one file.</summary>
    public async Task<FileObject> MetadataAsync(
        string database,
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var bytes = await client.SendRawBytesAsync(
            HttpMethod.Get,
            $"/api/v1/databases/{Escape(database)}/files/{Escape(id)}/metadata",
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        return Deserialize<FileObject>(bytes);
    }

    /// <summary>Downloads one verified file's bytes.</summary>
    public async Task<byte[]> DownloadAsync(
        string database,
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return await client.SendRawBytesAsync(
            HttpMethod.Get,
            $"/api/v1/databases/{Escape(database)}/files/{Escape(id)}",
            null,
            null,
            cancellationToken).ConfigureAwait(false);
    }

    private static T Deserialize<T>(byte[] bytes) =>
        JsonSerializer.Deserialize<T>(bytes, JsonOptions)
        ?? throw new JsonException("The TerraScale file response was empty.");

    private static string Escape(string value) => Uri.EscapeDataString(value.Trim());

    private sealed record FileObjectsResponse(IReadOnlyList<FileObject> Files);
}

/// <summary>Immutable metadata for one tenant-owned file object.</summary>
public sealed record FileObject(
    string Id,
    long Length,
    string Sha256,
    DateTimeOffset CreatedAt,
    string? ContentType,
    string? FileName);
