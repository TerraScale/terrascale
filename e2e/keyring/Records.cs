using System.Text.Json;
using System.Text.Json.Serialization;

namespace Journey.Keyring;

internal sealed record KeyringFile(string Active, Dictionary<string, string> Keys);

internal sealed record EvidenceRequest(string Database, string KeyId, string Marker);

internal sealed record InventoryEntry(string Id, string KeyId, string State, int Snapshots);

internal sealed record Inventory(string Active, string SystemKeyId, IReadOnlyList<InventoryEntry> Databases, IReadOnlyList<string> Shredded);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true)]
[JsonSerializable(typeof(KeyringFile))]
[JsonSerializable(typeof(EvidenceRequest))]
[JsonSerializable(typeof(Inventory))]
internal sealed partial class ProofJsonContext : JsonSerializerContext;
