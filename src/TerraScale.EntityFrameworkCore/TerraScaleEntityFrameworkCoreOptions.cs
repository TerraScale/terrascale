using System.Text.Json;
using Microsoft.EntityFrameworkCore.Metadata;
using TerraScale.DotNet;

namespace TerraScale.EntityFrameworkCore;

/// <summary>
/// Configures TerraScale Entity Framework Core source integration.
/// </summary>
public sealed class TerraScaleEntityFrameworkCoreOptions
{
    /// <summary>
    /// TerraScale API base address, for example <c>https://api.terrascale.tech</c>.
    /// </summary>
    public Uri BaseAddress { get; set; } = new("http://localhost:5294", UriKind.Absolute);

    /// <summary>
    /// Caller-provided TerraScale project API key. The integration never creates or rotates credentials.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Caller-provided bearer token for public API calls.
    /// </summary>
    public string? BearerToken { get; set; }

    /// <summary>
    /// TerraScale project id used with project API-key authentication.
    /// </summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// JSON serializer options used when materializing TerraScale document data into entity instances.
    /// </summary>
    public JsonSerializerOptions JsonSerializerOptions { get; set; } = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Optional fallback resolver for repository names when the entity type has no TerraScale repository annotation.
    /// </summary>
    public Func<IEntityType, string?>? RepositoryNameResolver { get; set; }

    internal TerraScaleEntityFrameworkCoreOptions Clone() =>
        new()
        {
            BaseAddress = BaseAddress,
            ApiKey = ApiKey,
            BearerToken = BearerToken,
            ProjectId = ProjectId,
            JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerOptions),
            RepositoryNameResolver = RepositoryNameResolver
        };

    internal TerraScaleClientOptions ToClientOptions() =>
        new()
        {
            BaseAddress = BaseAddress,
            ApiKey = ApiKey,
            BearerToken = BearerToken,
            ProjectId = ProjectId
        };
}
