using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraBase.Features.ApiKeys;

public sealed record CreateApiKeyInput(string? Name, IReadOnlyList<string>? Scopes, string? OrganizationId, string? ProjectId, string? DatabaseId, DateTimeOffset? ExpiresAt);

public sealed record RevokeApiKeyInput(string? Key);

public sealed record CreatedApiKeyBody(
    string Id, string Name, IReadOnlyList<string> Scopes, string OrganizationId, string? ProjectId, string? DatabaseId, string Secret, DateTimeOffset? ExpiresAt);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CreateApiKeyInput))]
[JsonSerializable(typeof(RevokeApiKeyInput))]
[JsonSerializable(typeof(CreatedApiKeyBody))]
internal sealed partial class ApiKeysJsonContext : JsonSerializerContext;
