using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Features.Documents;
using TerraScale.TerraBase.Features.Health;
using TerraScale.TerraBase.Features.Management;

namespace TerraScale.TerraBase.Hosting;

/// <summary>Source-generated JSON metadata for every request and response type (fast startup, no reflection-based serialization).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AcceptInvitationRequest))]
[JsonSerializable(typeof(ApiKey))]
[JsonSerializable(typeof(AuditEvent))]
[JsonSerializable(typeof(AuditRetention))]
[JsonSerializable(typeof(CreateApiKeyRequest))]
[JsonSerializable(typeof(CreatedApiKeyBody))]
[JsonSerializable(typeof(CreatedInvitation))]
[JsonSerializable(typeof(DatabaseBody))]
[JsonSerializable(typeof(DocumentBody))]
[JsonSerializable(typeof(DocumentPageBody))]
[JsonSerializable(typeof(DocumentVersionBody))]
[JsonSerializable(typeof(Invitation))]
[JsonSerializable(typeof(InvitationRequest))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(ManagedDatabase))]
[JsonSerializable(typeof(ManagementPage<AuditEvent>))]
[JsonSerializable(typeof(ManagementPage<Invitation>))]
[JsonSerializable(typeof(ManagementPage<ManagedDatabase>))]
[JsonSerializable(typeof(ManagementPage<Organization>))]
[JsonSerializable(typeof(ManagementPage<OrganizationMember>))]
[JsonSerializable(typeof(ManagementPage<Project>))]
[JsonSerializable(typeof(NameRequest))]
[JsonSerializable(typeof(Organization))]
[JsonSerializable(typeof(OrganizationMember))]
[JsonSerializable(typeof(OwnershipTransfer))]
[JsonSerializable(typeof(Project))]
[JsonSerializable(typeof(RoleRequest))]
[JsonSerializable(typeof(StatusBody))]
[JsonSerializable(typeof(StoredInvitation))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(SwitchOrganizationRequest))]
[JsonSerializable(typeof(TransferRequest))]
internal sealed partial class TerraBaseJsonContext : JsonSerializerContext;
