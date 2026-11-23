using System.Text.Json;
using System.Text.Json.Serialization;
using Cysharp.Serialization.Json;
using TerraScale.Contracts.ControlPlane;
using TerraScale.Contracts.Http;

namespace TerraScale.Contracts;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(UlidJsonConverter)])]
[JsonSerializable(typeof(ControlSnapshot))]
[JsonSerializable(typeof(ControlSnapshotAck))]
[JsonSerializable(typeof(SqlExecuteRequest))]
[JsonSerializable(typeof(SqlExecuteResponse))]
[JsonSerializable(typeof(SqlWsClientFrame))]
[JsonSerializable(typeof(SqlWsServerFrame))]
[JsonSerializable(typeof(ApiErrorEnvelope))]
public sealed partial class TerraScaleContractsJsonContext : JsonSerializerContext;
