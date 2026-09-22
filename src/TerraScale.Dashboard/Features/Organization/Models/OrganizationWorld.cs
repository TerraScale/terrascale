using System.Collections.Immutable;
using TerraScale.Dashboard.Features.Data;

namespace TerraScale.Dashboard.Features.Organization.Models;

public enum OrganizationKind { Personal, Business }

/// <summary>Organization-owned resources and billing. Switching never copies another workspace's allowance.</summary>
public sealed class OrganizationWorld
{
    public string Id { get; set; } = "";
    public OrganizationKind Kind { get; set; } = OrganizationKind.Business;
    public string? PersonalOwnerId { get; set; }
    public bool Archived { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string BillingStatus { get; set; } = "billing_required";
    public string PlanId { get; set; } = "plan_free";

    /// <summary>Stripe billing-catalog key (starter|pro|business) chosen at
    /// checkout. Distinct from <see cref="PlanId"/>, which stays the platform
    /// plan the organization provisions against.</summary>
    public string PlanKey { get; set; } = "";
    public bool PaymentMethodValidated { get; set; }
    public string? BillingRenewal { get; set; }
    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public string? CheckoutSessionId { get; set; }
    public string? CheckoutReturnPath { get; set; }
    public Dictionary<string, DashboardStore.OwnershipTransfer> OwnershipTransfers { get; set; } = [];

    public bool OrganizationDeletionRequested { get; set; }

    public string ActiveOrganizationName { get; set; } = "Personal";

    public ImmutableArray<DashboardProject> Projects { get; set; } = [];

    public string ActiveProjectId { get; set; } = "";

    public ImmutableArray<DashboardDatabase> Databases { get; set; } = [];

    public ImmutableArray<ProjectApiKey> ApiKeys { get; set; } = [];

    public ImmutableArray<M2mApplication> M2mApplications { get; set; } = [];

    public ImmutableArray<TeamMember> TeamMembers { get; set; } = [];

    public ImmutableArray<Invitation> Invitations { get; set; } = [];

    public ImmutableArray<AuditEvent> AuditEvents { get; set; } = [];

    public UsageCycle Usage { get; set; } = new(
        Period: "Current cycle",
        StorageMb: 0,
        StorageLimitMb: 0,
        Requests: 0,
        RequestLimit: 0,
        ReadUnits: 0,
        WriteUnits: 0,
        InvoiceStatus: "",
        OverrunEstimateUsd: 0m);

    public ImmutableArray<(string Range, ImmutableArray<WorkloadPoint> Points)> Workloads { get; set; } = [];

    public ImmutableArray<SavedQuery> SavedQueries { get; set; } = [];

    public ImmutableArray<WebhookEndpoint> Webhooks { get; set; } = [];

    public ImmutableArray<M2mCredential> M2mCredentials { get; set; } = [];

    public (string CredentialId, string Name, string Secret)? PendingSecret { get; set; }

    public ImmutableArray<BackupRestorePoint> RestorePoints { get; set; } = [];

    public ImmutableArray<BackupRestoreJob> RestoreJobs { get; set; } = [];

    public ImmutableArray<RestorePlan> RestorePlans { get; set; } = [];

    public string BackupsLoadedAt { get; set; } = "";

    public ImmutableArray<BackupJob> BackupJobs { get; set; } = [];

    public ImmutableArray<ExplorerDocument> ExplorerDocuments { get; set; } = [];

    public SchemaDocument Schema { get; set; } = new([], Version: 1, UpdatedAt: "", Migration: null);

    public SchemaDiff? PendingSchemaDiff { get; set; }

    public string ProposedSchemaJson { get; set; } = "";

    public ImmutableArray<IndexState> Indexes { get; set; } = [];

    public ImmutableArray<QueryTemplate> QueryTemplates { get; set; } = [];

    public ImmutableArray<IndexRecommendation> IndexRecommendations { get; set; } = [];

    public ImmutableArray<DashboardStore.InvoiceRow> Invoices { get; set; } = [];

    public bool PaidExcessEnabled { get; set; }

    public string? SpendCapUsd { get; set; }

    public Dictionary<string, string> DatabaseSpendCaps { get; set; } = [];

    public HashSet<string> BackupsDisabled { get; set; } = [];

    public Dictionary<string, (string Schedule, string Retention)> BackupPolicies { get; set; } = [];
}
