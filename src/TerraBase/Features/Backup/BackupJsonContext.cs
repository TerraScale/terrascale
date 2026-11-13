using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Hosting;

namespace TerraBase.Features.Backup;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, MaxDepth = RequestJson.StoredMaxDepth + 8)]
[JsonSerializable(typeof(BackupStatus))]
[JsonSerializable(typeof(BackupStatusRecord))]
[JsonSerializable(typeof(BackupAccount))]
[JsonSerializable(typeof(PointInTimePlan))]
[JsonSerializable(typeof(ProjectBackupDestination))]
[JsonSerializable(typeof(ProjectBackupDestinationRecord))]
[JsonSerializable(typeof(SealedCredentials))]
[JsonSerializable(typeof(CustomerKeyPair))]
[JsonSerializable(typeof(ProjectBackupDestinationRequest))]
[JsonSerializable(typeof(RestoreWindow))]
internal sealed partial class BackupJsonContext : JsonSerializerContext;
