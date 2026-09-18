using TerraScale.DotNet.Generated;

#pragma warning disable CS1591
namespace TerraScale.DotNet;

/// <summary>Named administration operations backed by the public contract.</summary>
public sealed class TerraScaleManagementClient
{
    public TerraScaleManagementClient(TerraScaleClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        Databases = client.Databases;
        Collections = client.Collections;
        Backups = client.Backups;
        Exports = new(client);
        Imports = new(client);
        Usage = new(client);
        Schema = new(client);
        Indexes = new(client);
        Webhooks = new(client);
        Development = new(client);
    }
    public TerraScaleDatabasesClient Databases { get; }
    public TerraScaleCollectionsClient Collections { get; }
    public TerraScaleBackupsClient Backups { get; }
    public TerraScaleManagementExportsClient Exports { get; }
    public TerraScaleManagementImportsClient Imports { get; }
    public TerraScaleUsageClient Usage { get; }
    public TerraScaleSchemaClient Schema { get; }
    public TerraScaleIndexesClient Indexes { get; }
    public TerraScaleWebhooksClient Webhooks { get; }
    public TerraScaleDevelopmentClient Development { get; }
}

public sealed class TerraScaleUsageClient(TerraScaleClient client)
{
    public Task<UsageSummary> GetAsync(string database, CancellationToken cancellationToken = default) =>
        client.SendJsonAsync(HttpMethod.Get, TerraScaleManagementPaths.Database(database) + "/usage", TerraScaleContractJsonContext.Default.UsageSummary, cancellationToken);
}

internal static class TerraScaleManagementPaths
{
    public static string Escape(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Uri.EscapeDataString(value.Trim());
    }
    public static string Database(string database) => "/api/v1/databases/" + Escape(database);
    public static string Collection(string database, string collection) => Database(database) + "/collections/" + Escape(collection);
}
