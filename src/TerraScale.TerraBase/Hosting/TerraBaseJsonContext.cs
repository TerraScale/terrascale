using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Features.Documents;
using TerraScale.TerraBase.Features.Health;
using TerraScale.TerraBase.Features.Management;
using TerraScale.TerraBase.Features.Management.Backups;
using TerraScale.TerraBase.Features.Management.Credentials;
using TerraScale.TerraBase.Features.Management.Indexes;
using TerraScale.TerraBase.Features.Management.Queries;
using TerraScale.TerraBase.Features.Management.Schema;
using TerraScale.TerraBase.Features.Management.Usage;
using TerraScale.TerraBase.Features.Pressure;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Hosting;

/// <summary>Source-generated JSON metadata for every request and response type (fast startup, no reflection-based serialization).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AcceptInvitationRequest))]
[JsonSerializable(typeof(AddMemberRequest))]
[JsonSerializable(typeof(ApiKey))]
[JsonSerializable(typeof(AppliedIndexRecommendation))]
[JsonSerializable(typeof(AuditEvent))]
[JsonSerializable(typeof(AuditRetention))]
[JsonSerializable(typeof(BackupPolicy))]
[JsonSerializable(typeof(BackupPolicyRequest))]
[JsonSerializable(typeof(BackupRecord))]
[JsonSerializable(typeof(BackupSchedule))]
[JsonSerializable(typeof(CollectionList))]
[JsonSerializable(typeof(CollectionSchema))]
[JsonSerializable(typeof(CreateApiKeyRequest))]
[JsonSerializable(typeof(CreatedApiKeyBody))]
[JsonSerializable(typeof(CreatedInvitation))]
[JsonSerializable(typeof(CreatedProjectApiKey))]
[JsonSerializable(typeof(CreateIndexRequest))]
[JsonSerializable(typeof(CreateProjectApiKeyRequest))]
[JsonSerializable(typeof(DatabaseBackup))]
[JsonSerializable(typeof(ErrorBody))]
[JsonSerializable(typeof(ShapeBody))]
[JsonSerializable(typeof(DatabaseBody))]
[JsonSerializable(typeof(DatabaseOwner))]
[JsonSerializable(typeof(DatabaseUsage))]
[JsonSerializable(typeof(CollectionBody))]
[JsonSerializable(typeof(CollectionPageBody))]
[JsonSerializable(typeof(CreateCollectionRequest))]
[JsonSerializable(typeof(DocumentBody))]
[JsonSerializable(typeof(DocumentPageBody))]
[JsonSerializable(typeof(IndexBody))]
[JsonSerializable(typeof(IndexListBody))]
[JsonSerializable(typeof(IndexRecommendationListBody))]
[JsonSerializable(typeof(Invitation))]
[JsonSerializable(typeof(InvitationRequest))]
[JsonSerializable(typeof(WriteBody))]
[JsonSerializable(typeof(QueryRequest))]
[JsonSerializable(typeof(IReadOnlyList<QuerySortRequest>))]
[JsonSerializable(typeof(IReadOnlyList<JsonElement>))]
[JsonSerializable(typeof(PatchOpsRequest))]
[JsonSerializable(typeof(TransactionRequest))]
[JsonSerializable(typeof(TransactionOpRequest))]
[JsonSerializable(typeof(TransactionBody))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(ManagedDatabase))]
[JsonSerializable(typeof(ManagedDatabaseBody))]
[JsonSerializable(typeof(ManagementPage<AuditEvent>))]
[JsonSerializable(typeof(ManagementPage<DatabaseBackup>))]
[JsonSerializable(typeof(ManagementPage<Invitation>))]
[JsonSerializable(typeof(ManagementPage<ManagedDatabase>))]
[JsonSerializable(typeof(ManagementPage<ManagedDatabaseBody>))]
[JsonSerializable(typeof(ManagementPage<Organization>))]
[JsonSerializable(typeof(ManagementPage<OrganizationMember>))]
[JsonSerializable(typeof(ManagementPage<Project>))]
[JsonSerializable(typeof(ManagementPage<ProjectApiKey>))]
[JsonSerializable(typeof(ManagementPage<SavedQuery>))]
[JsonSerializable(typeof(NameRequest))]
[JsonSerializable(typeof(OpCounts))]
[JsonSerializable(typeof(Organization))]
[JsonSerializable(typeof(OrganizationMember))]
[JsonSerializable(typeof(OwnershipTransfer))]
[JsonSerializable(typeof(PlanRequest))]
[JsonSerializable(typeof(PressureBody))]
[JsonSerializable(typeof(Project))]
[JsonSerializable(typeof(ProjectApiKey))]
[JsonSerializable(typeof(ProjectKeyEntry))]
[JsonSerializable(typeof(ProjectUsage))]
[JsonSerializable(typeof(QueryFieldUsage))]
[JsonSerializable(typeof(RoleRequest))]
[JsonSerializable(typeof(SavedQuery))]
[JsonSerializable(typeof(SavedQueryEntry))]
[JsonSerializable(typeof(SavedQueryRequest))]
[JsonSerializable(typeof(SqlCheckBody))]
[JsonSerializable(typeof(SqlRequest))]
[JsonSerializable(typeof(SqlResultBody))]
[JsonSerializable(typeof(StatusBody))]
[JsonSerializable(typeof(StoredInvitation))]
[JsonSerializable(typeof(TenantContext))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(SweepEntry))]
[JsonSerializable(typeof(SwitchOrganizationRequest))]
[JsonSerializable(typeof(TransferRequest))]
internal sealed partial class TerraBaseJsonContext : JsonSerializerContext;
