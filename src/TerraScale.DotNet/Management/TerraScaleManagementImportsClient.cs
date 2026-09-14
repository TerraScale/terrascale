using System.Net;
using TerraScale.DotNet.Generated;

#pragma warning disable CS1591
namespace TerraScale.DotNet;

/// <summary>Durable database import jobs and their binary input artifacts.</summary>
public sealed class TerraScaleManagementImportsClient(TerraScaleClient client)
{
    public Task<ImportJob> CreateAsync(
        string database,
        string format,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        return client.SendJsonAsync(
            HttpMethod.Post,
            Route(database),
            new CreateImport(ParseFormat(format), idempotencyKey.Trim()),
            TerraScaleContractJsonContext.Default.CreateImport,
            TerraScaleContractJsonContext.Default.ImportJob,
            cancellationToken,
            idempotencyKey.Trim());
    }

    private static TransferFormat ParseFormat(string value) => value.Trim() switch
    {
        "native" => TransferFormat.Native,
        "jsonl" => TransferFormat.Jsonl,
        "csv" => TransferFormat.Csv,
        "mongo_json" => TransferFormat.MongoJson,
        "cosmos_json" => TransferFormat.CosmosJson,
        "dynamo_json" => TransferFormat.DynamoJson,
        _ => throw new ArgumentException($"Unsupported import format '{value}'.", nameof(value)),
    };

    public Task<IReadOnlyList<ImportJob>> ListAsync(string database, CancellationToken cancellationToken = default) =>
        client.SendJsonAsync(
            HttpMethod.Get,
            Route(database),
            TerraScaleContractJsonContext.Default.IReadOnlyListImportJob,
            cancellationToken);

    public Task<ImportJob> GetAsync(string database, string importId, CancellationToken cancellationToken = default) =>
        client.SendJsonAsync(
            HttpMethod.Get,
            Job(database, importId),
            TerraScaleContractJsonContext.Default.ImportJob,
            cancellationToken);

    /// <summary>Uploads the immutable import input as an octet-stream request.</summary>
    public Task<ImportJob> UploadAsync(
        string database,
        string importId,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default) =>
        client.UploadImportAsync(Job(database, importId) + "/input", content, cancellationToken);

    /// <summary>Uploads an import input stream. The stream is read before the request is sent.</summary>
    public async Task<ImportJob> UploadAsync(
        string database,
        string importId,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead) throw new ArgumentException("The import input stream must be readable.", nameof(content));
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return await UploadAsync(database, importId, buffer.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public Task<ImportJob> ResumeAsync(string database, string importId, CancellationToken cancellationToken = default) =>
        client.SendJsonAsync(
            HttpMethod.Post,
            Job(database, importId) + "/resume",
            TerraScaleContractJsonContext.Default.ImportJob,
            cancellationToken,
            idempotencyKey: string.Empty);

    private static string Route(string database) => TerraScaleManagementPaths.Database(database) + "/imports";
    private static string Job(string database, string importId) => Route(database) + "/" + TerraScaleManagementPaths.Escape(importId);
}
