using System.Text.Json;
using Microsoft.EntityFrameworkCore.Metadata;
using TerraScale.DotNet;

namespace TerraScale.EntityFrameworkCore;

/// <summary>Configures the EF Core integration over TerraScale's public database API.</summary>
public sealed class TerraScaleEntityFrameworkCoreOptions
{
    /// <summary>TerraScale API origin.</summary>
    public Uri BaseAddress { get; set; } = new("https://api.terrascale.tech");
    /// <summary>Project API key, when API-key authentication is used.</summary>
    public string? ApiKey { get; set; }
    /// <summary>Bearer token, when bearer authentication is used.</summary>
    public string? BearerToken { get; set; }
    /// <summary>Refreshable bearer token callback.</summary>
    public Func<CancellationToken, ValueTask<string>>? BearerTokenProvider { get; set; }
    /// <summary>Project selector required by project-scoped API keys.</summary>
    public string? ProjectId { get; set; }
    /// <summary>Database used by the mapped entities.</summary>
    public string? DatabaseName { get; set; }
    /// <summary>JSON options used to materialize document values.</summary>
    public JsonSerializerOptions JsonSerializerOptions { get; set; } = new(JsonSerializerDefaults.Web);
    /// <summary>Fallback collection resolver.</summary>
    public Func<IEntityType, string?>? CollectionNameResolver { get; set; }

    internal TerraScaleEntityFrameworkCoreOptions Clone() => new()
    {
        BaseAddress = BaseAddress,
        ApiKey = ApiKey,
        BearerToken = BearerToken,
        BearerTokenProvider = BearerTokenProvider,
        ProjectId = ProjectId,
        DatabaseName = DatabaseName,
        JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerOptions),
        CollectionNameResolver = CollectionNameResolver
    };

    internal TerraScaleClientOptions ToClientOptions() => new()
    {
        BaseAddress = BaseAddress,
        ApiKey = ApiKey,
        BearerToken = BearerToken,
        BearerTokenProvider = BearerTokenProvider,
        ProjectId = ProjectId
    };
}
