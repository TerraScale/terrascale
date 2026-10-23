using System.Security.Cryptography;
using System.Text;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraScale.Endpoints;
using ZLogger;

namespace TerraBase.Features.Replication;

/// <summary>An explicit retry of an existing durable tenant rebuild, pinned to its faulted incarnation.</summary>
internal sealed record ReplicaRecoveryRequest(string? FaultedIncarnation, string? Reason);

/// <summary>Retries certified tenant recovery without changing catalog trust or creating fault records.</summary>
internal sealed class RetryReplicaRecoveryEndpoint(
    IManagementService management, IRebuildLedger ledger, IReplicaRebuild rebuild, ICatalogGate gate, ICatalogFreshness freshness,
    ILogger<RetryReplicaRecoveryEndpoint> logger) : Endpoint<ReplicaRecoveryRequest, IResult>
{
    private static readonly TimeSpan CatalogWindow = TimeSpan.FromSeconds(30);

    public override void Configure()
    {
        Post(ManagementRoute.Database + "/recovery/retry");
        Name("retryReplicaRecovery");
        Summary("Retries an existing certified tenant rebuild");
        Description("Internal operator only, acting as an existing owner or admin of the exact organization/project/database IDs. For ts-auth-enrolled projects, also requires that acting user’s current sign-in Bearer token and live ts-auth organization owner/admin authority. Requires an open trusted catalog and peer freshness proofs younger than 30 seconds. faultedIncarnation must match an existing durable rebuild; reason is 1–256 characters with no controls. Never restores SYSTEM, changes freshness, creates a fault or registers a database. 204 after certification; 409 if busy, still pending, mismatched or already completed. Replaying a completed request cannot start another rebuild. Audit logs contain the acting user, target IDs, reason SHA-256 and fixed outcome, never the reason text or credentials.");
        Tags("Administration");
        Produces(StatusCodes.Status204NoContent);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Internal);
        Metadata(new OperatorAuthorityProof());
    }

    public override async ValueTask<IResult> HandleAsync(ReplicaRecoveryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!HttpContext.User.IsOperator())
        {
            return TypedResults.Unauthorized();
        }

        var user = ManagementRoute.UserId(HttpContext);
        var organization = ManagementRoute.OrganizationId(HttpContext);
        var project = ManagementRoute.Value(HttpContext, "project");
        var id = ManagementRoute.Value(HttpContext, "database");
        var outcome = "denied";
        string? reasonHash = null;
        try
        {
            if (!gate.IsOpen || !gate.Trusted || !await freshness.IsFreshAsync(CatalogWindow, cancellationToken))
            {
                outcome = "catalog_stale";
                return ErrorResponse.From(new Unavailable("trusted current catalog required", "catalog_stale"));
            }

            var authorized = await management.GetRecoveryDatabaseTenantAsync(user, organization, project, id, OperatorAuthorityProof.Read(HttpContext.Request), cancellationToken);
            if (authorized is not TenantContext database)
            {
                return authorized switch
                {
                    NotFound error => ErrorResponse.From(error),
                    Forbidden error => ErrorResponse.From(error),
                    Unavailable error => ErrorResponse.From(error),
                    TenantContext => throw new System.Diagnostics.UnreachableException(),
                };
            }

            if (!EntityIds.IsValid(request.FaultedIncarnation) || request.Reason is not { Length: > 0 and <= 256 } reason
                || string.IsNullOrWhiteSpace(reason) || reason.Any(char.IsControl))
            {
                outcome = "invalid_request";
                return ErrorResponse.From(new Validation("faultedIncarnation must be a canonical ULID; reason must be 1 to 256 characters without controls"));
            }

            reasonHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(reason.Trim())));
            var record = await ledger.GetAsync(database, cancellationToken);
            if (record is null || record.OldIncarnation != Ulid.Parse(request.FaultedIncarnation!))
            {
                outcome = "no_matching_rebuild";
                return ErrorResponse.From(new Conflict("no matching pending rebuild", "no_matching_rebuild"));
            }

            if (!gate.IsOpen || !gate.Trusted || !await freshness.IsFreshAsync(CatalogWindow, cancellationToken))
            {
                outcome = "catalog_stale";
                return ErrorResponse.From(new Unavailable("trusted current catalog required", "catalog_stale"));
            }

            outcome = "failed";
            logger.ZLogInformation($"Tenant recovery: operator {user:@Operator}, organization {organization:@Organization}, project {project:@Project}, database {id:@Database}, reason SHA-256 {reasonHash:@ReasonHash}, action retry_certified_rebuild, outcome started");
            var result = await rebuild.RebuildAsync(database, record.OldIncarnation, cancellationToken);
            outcome = result switch
            {
                RebuildOutcome.Completed => "completed",
                RebuildOutcome.Busy => "busy",
                _ => "pending",
            };
            return result is RebuildOutcome.Completed
                ? TypedResults.NoContent()
                : ErrorResponse.From(new Conflict("certified recovery remains pending; no bypass was applied", "recovery_" + outcome));
        }
        finally
        {
            logger.ZLogInformation($"Tenant recovery: operator {user:@Operator}, organization {organization:@Organization}, project {project:@Project}, database {id:@Database}, reason SHA-256 {reasonHash:@ReasonHash}, action retry_certified_rebuild, outcome {outcome:@Outcome}");
        }
    }
}
