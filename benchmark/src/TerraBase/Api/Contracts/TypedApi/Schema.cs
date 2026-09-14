// <copyright file="TypedApi.Schema.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using System.Text.Json;
using TerraBase.Protocol;

public sealed partial class TypedApi
{
    // The schema surface (SM-04): the typed schema lifecycle
    // ------------------------------------------------------------------

    internal async Task<IResult> SchemaStatusAsync(HttpContext context, string collection, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        if (this.Scope(context, collection) is { } invalid)
        {
            return invalid;
        }

        var caller = ResolveCaller(context);
        try
        {
            var status = await this.supervisor
                .CallAsync(caller.Tenant, caller.Principal, "schema.status", channel => channel.SchemaStatusAsync(collection, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(new SchemaStatusDto(status.EnforcedVersion, status.RuleCount));
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> SchemaDeclareAsync(HttpContext context, string collection, SchemaDeclareDto request, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        if (this.Scope(context, collection) is { } invalid)
        {
            return invalid;
        }

        if (Idempotency(context) is { } badKey)
        {
            return badKey;
        }

        var caller = ResolveCaller(context);
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        try
        {
            // A JSON null clears the policy. It must stay a nullable protocol
            // field rather than becoming the typed TbepValue.Null value.
            var schema = request.Schema switch
            {
                null => null,
                { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
                { } document => JsonValues.ToValue(document),
            };
            var answer = await this.supervisor
                .CallAsync(
                    caller.Tenant,
                    caller.Principal,
                    "schema.declare",
                    channel => channel.SchemaDeclareAsync(collection, schema, idempotencyKey, idempotencyKey, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            this.audit.Record(
                AuditKind.SchemaLifecycle,
                caller.Tenant,
                caller.Principal,
                operation: "schema.declare",
                outcome: AuditOutcome.Accepted,
                detail: $"collection={collection}");
            return Results.Json(new SchemaStatusDto(answer.EnforcedVersion, answer.RuleCount));
        }
        catch (TbepException failure)
        {
            return this.SchemaFailure(failure, caller, collection, "schema.declare");
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> SchemaValidateAsync(HttpContext context, string collection, SchemaValidateDto request, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        if (this.Scope(context, collection) is { } invalid)
        {
            return invalid;
        }

        if (request.Document is not { } document)
        {
            var missing = ResolveCaller(context);
            this.audit.Record(
                AuditKind.ScopeRefused,
                missing.Tenant,
                missing.Principal,
                operation: "schema.validate",
                outcome: AuditOutcome.Refused,
                detail: "the document to validate is missing");
            return Results.Json(
                new ErrorDto("document-missing", "schema validation names the document value it validates"),
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var caller = ResolveCaller(context);
        try
        {
            var answer = await this.supervisor
                .CallAsync(
                    caller.Tenant,
                    caller.Principal,
                    "schema.validate",
                    channel => channel.SchemaValidateAsync(collection, JsonValues.ToValue(document), cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(new SchemaValidationDto(
                answer.EnforcedVersion,
                answer.Valid,
                [.. answer.Violations.Select(violation => new ViolationDto(violation.Field, violation.Kind.Variant.ToSnakeCase()))]));
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> MigrationStatusAsync(HttpContext context, string collection, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        if (this.Scope(context, collection) is { } invalid)
        {
            return invalid;
        }

        var caller = ResolveCaller(context);
        try
        {
            var status = await this.supervisor
                .CallAsync(caller.Tenant, caller.Principal, "migration.status", channel => channel.MigrationStatusAsync(collection, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(new MigrationStatusDto(MigrationStateDto.Of(status.State)));
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> MigrationDryRunAsync(HttpContext context, string collection, MigrationDryRunDto request, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        if (this.Scope(context, collection) is { } invalid)
        {
            return invalid;
        }

        if (request.Contract is not { } dryRunContract)
        {
            return this.MigrationContractMissing(context, "migration.dry-run");
        }

        if (this.MigrationCollection(context, collection, dryRunContract.Collection, "migration.dry-run") is { } invalidCollection)
        {
            return invalidCollection;
        }

        var caller = ResolveCaller(context);
        try
        {
            var contract = dryRunContract.ToContract();
            var answer = await this.supervisor
                .CallAsync(caller.Tenant, caller.Principal, "migration.dry_run", channel => channel.MigrationDryRunAsync(contract, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(new { plan = MigrationPlanDto.Of(answer.Plan) });
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> MigrationApplyAsync(HttpContext context, string collection, MigrationApplyDto request, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        if (this.Scope(context, collection) is { } invalid)
        {
            return invalid;
        }

        if (Idempotency(context) is { } badKey)
        {
            return badKey;
        }

        if (request.Contract is not { } applyContract)
        {
            return this.MigrationContractMissing(context, "migration.apply");
        }

        if (this.MigrationCollection(context, collection, applyContract.Collection, "migration.apply") is { } invalidCollection)
        {
            return invalidCollection;
        }

        var caller = ResolveCaller(context);
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        try
        {
            var contract = applyContract.ToContract();
            var answer = await this.supervisor
                .CallAsync(
                    caller.Tenant,
                    caller.Principal,
                    "migration.apply",
                    channel => channel.MigrationApplyAsync(contract, request.AcknowledgeNotRollbackable, idempotencyKey, idempotencyKey, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            this.audit.Record(
                AuditKind.SchemaLifecycle,
                caller.Tenant,
                caller.Principal,
                operation: "migration.apply",
                outcome: AuditOutcome.Accepted,
                detail: $"collection={collection}");
            return Results.Json(new { receipt = MigrationReceiptDto.Of(answer.Receipt) });
        }
        catch (TbepException failure)
        {
            return this.SchemaFailure(failure, caller, collection, "migration.apply");
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    /// <summary>Refuses a migration request whose contract is absent, after the local gate has run.</summary>
    /// <param name="context">The request context.</param>
    /// <param name="operation">The operation name.</param>
    /// <returns>The refusal answer.</returns>
    private IResult MigrationContractMissing(HttpContext context, string operation)
    {
        var caller = ResolveCaller(context);
        this.audit.Record(
            AuditKind.ScopeRefused,
            caller.Tenant,
            caller.Principal,
            operation,
            AuditOutcome.Refused,
            "the migration contract is missing");
        return Results.Json(
            new ErrorDto("contract-missing", "a migration names the contract it runs"),
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    /// <summary>Refuses a migration contract that names a collection other than the route scope.</summary>
    /// <param name="context">The request context.</param>
    /// <param name="routeCollection">The collection from the route.</param>
    /// <param name="contractCollection">The collection from the migration contract.</param>
    /// <param name="operation">The migration operation.</param>
    /// <returns>The refusal answer, or <c>null</c> when both collection scopes agree.</returns>
    private IResult? MigrationCollection(HttpContext context, string routeCollection, string? contractCollection, string operation)
    {
        if (string.Equals(routeCollection, contractCollection, StringComparison.Ordinal))
        {
            return null;
        }

        var caller = ResolveCaller(context);
        this.audit.Record(
            AuditKind.ScopeRefused,
            caller.Tenant,
            caller.Principal,
            operation,
            AuditOutcome.Refused,
            "the migration route collection does not match the contract collection");
        return Results.Json(
            new ErrorDto("collection-mismatch", "the migration route collection must match the contract collection"),
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    /// <summary>Maps a schema-surface engine refusal: the engine-enforced tenant isolation is surfaced typed and audited.</summary>
    /// <param name="failure">The typed engine failure.</param>
    /// <param name="caller">The caller.</param>
    /// <param name="collection">The collection.</param>
    /// <param name="operation">The operation name.</param>
    /// <returns>The refusal answer.</returns>
    private IResult SchemaFailure(TbepException failure, Caller caller, string collection, string operation)
    {
        if (failure.Detail.Contains("another channel", StringComparison.Ordinal))
        {
            this.audit.Record(
                AuditKind.TenantRefused,
                caller.Tenant,
                caller.Principal,
                operation,
                AuditOutcome.Refused,
                "tenant lifecycle refused");
            return Results.Json(
                new ErrorDto("tenant-refused", $"the schema surface of {collection} belongs to another tenant's channel"),
                statusCode: StatusCodes.Status403Forbidden);
        }

        return this.Failure(failure, caller);
    }

    // ------------------------------------------------------------------

}
