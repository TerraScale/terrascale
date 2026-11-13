using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Hosting;

namespace TerraBase.Features.Replicas;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, MaxDepth = RequestJson.StoredMaxDepth + 8)]
[JsonSerializable(typeof(HomePromoteRequest))]
[JsonSerializable(typeof(HomeAbortRequest))]
[JsonSerializable(typeof(HomePromoteAnswer))]
[JsonSerializable(typeof(ReplicaCatalogPage))]
[JsonSerializable(typeof(ReplicaCatalogEntry))]
[JsonSerializable(typeof(ReplicaRevisionNotice))]
[JsonSerializable(typeof(HomeSessionOpen))]
[JsonSerializable(typeof(NodeLease))]
[JsonSerializable(typeof(RegionalOwner))]
[JsonSerializable(typeof(RegionalTransfer))]
[JsonSerializable(typeof(RegionalPrepared))]
[JsonSerializable(typeof(RegionalWriter))]
[JsonSerializable(typeof(RetentionStatus))]
[JsonSerializable(typeof(RetentionExpiryRequest))]
[JsonSerializable(typeof(RetentionExpiryAnswer))]
[JsonSerializable(typeof(RetainedManifest))]
[JsonSerializable(typeof(RetainedCertification))]
[JsonSerializable(typeof(RetainedPage))]
[JsonSerializable(typeof(RegionalPosition))]
[JsonSerializable(typeof(RegionalPrepare))]
[JsonSerializable(typeof(RegionalStatus))]
[JsonSerializable(typeof(RegionalDrain))]
[JsonSerializable(typeof(RegionalCatalogSnapshot))]
[JsonSerializable(typeof(HomeLease))]
[JsonSerializable(typeof(HeldRevisionAnswer))]
[JsonSerializable(typeof(ReplicaHeldNotice))]
[JsonSerializable(typeof(HomeSessionCall))]
[JsonSerializable(typeof(HomeSessionAnswer))]
[JsonSerializable(typeof(ReplicaChecksumPage))]
[JsonSerializable(typeof(ReplicaStatusReport))]
internal sealed partial class ReplicasJsonContext : JsonSerializerContext;
