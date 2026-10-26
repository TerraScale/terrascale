using System.Diagnostics;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Memory;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Queries;

/// <summary>Explains a database's SQL statement without running it.</summary>
public sealed class ExplainSqlEndpoint(ITenantDirectory tenants, IQueryService queries, IMemoryBudget memory, TerraBaseOptions options, TimeProvider time) : EndpointWithoutRequest<IResult>
{
    // A 16,384-character SQL statement fits even when every character is a six-byte JSON escape.
    private const int MaxBodyBytes = 128 * 1024;

    /// <inheritdoc />
    public override void Configure()
    {
        Post(ProjectApiRoute.Sql + "/explain");
        Name("explainSql");
        Summary("Explains a read-only SQL statement on a database");
        Description(QueriesExtensions.SqlDescription + " Returns the engine plan and the names of the secondary indexes it actually reads, plus suggestions for unindexed fields. "
            + "Does not execute the query, meter a document read or record index recommendation usage. Invalid SQL answers 400 invalid_request; an exhausted engine work budget answers 400 invalid_query. "
            + "Requires a ts-auth project access token with databases:read. A missing or mismatched project_id or database restriction answers 404. "
            + "The body must arrive within 30 seconds; a stalled upload answers 408 request_timeout.");
        Tags("Queries");
        Metadata(new RequestBodyMetadata(typeof(SqlRequest), "application/json"));
        Produces<SqlCheckBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status408RequestTimeout);
        Produces<ErrorBody>(StatusCodes.Status413PayloadTooLarge);
        Produces<ErrorBody>(StatusCodes.Status415UnsupportedMediaType);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var project = HttpContext.Request.RouteValues["project"] as string;
        var name = HttpContext.Request.RouteValues["database"] as string;
        var token = HttpContext.Features.Get<ProjectToken>();
        if ((!Primitives.EntityIds.IsValid(name) && !DatabaseName.TryParse(name, out _)) || token is null || token.ProjectId != project)
        {
            return ErrorResponse.From(new NotFound());
        }

        if (!token.Grants("databases:read"))
        {
            return ErrorResponse.From(new Forbidden("the token does not grant databases:read", "forbidden_scope"));
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        using var upload = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var found = await tenants.ResolveProjectDatabaseAsync(project!, name!, upload.Token);
            if (found is not ResolvedProjectDatabase resolved)
            {
                return found switch
                {
                    NotFound => ErrorResponse.From(new NotFound()),
                    Unavailable error => ErrorResponse.From(error),
                    ResolvedProjectDatabase => throw new UnreachableException(),
                };
            }

            var database = resolved.Database;
            if (HttpContext.Features.Get<ProjectAccess>() is not { } access
                || access.DatabaseName is not null && access.DatabaseId != database.DatabaseId)
            {
                return ErrorResponse.From(new NotFound());
            }
            if (!resolved.Placement.Includes(options.Region))
            {
                return DatabaseRegions.WrongRegion(resolved.Placement.Home, resolved.Placement.Regions);
            }

            UsageRequestMetering.Attribute(HttpContext, database);

            if (!HttpContext.Request.HasJsonContentType() || HttpContext.Request.Headers.ContainsKey("Content-Encoding"))
            {
                return ErrorResponse.From(new UnsupportedMediaType("the body must be uncompressed application/json"));
            }

            if (HttpContext.Request.ContentLength > MaxBodyBytes)
            {
                return TooLarge();
            }

            using var reservation = memory.TryReserve(MemoryLane.Foreground, MaxBodyBytes * 8);
            if (reservation is null)
            {
                return ErrorResponse.From(new Unavailable("no memory available for EXPLAIN"));
            }

            // Bound decoded body bytes with one overflow byte; Kestrel's limit also counts chunk framing.
            var body = new byte[MaxBodyBytes + 1];
            var length = 0;
            // Revalidate within the admission deadline, without initializing storage.
            if (await CheckCurrentAsync(database, name!, upload.Token) is { } changed)
            {
                return changed;
            }

            int read;
            while ((read = await HttpContext.Request.Body.ReadAsync(body.AsMemory(length), upload.Token)) > 0)
            {
                length += read;
                if (length > MaxBodyBytes)
                {
                    return TooLarge();
                }
            }

            upload.Token.ThrowIfCancellationRequested();
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);

            if (!RequestJson.IsText(body.AsSpan(0, length)))
            {
                return ErrorResponse.From(new Validation(RequestJson.TextRule));
            }

            return RequestJson.Read<SqlRequest>(body.AsSpan(0, length), "invalid_request") switch
            {
                Validation error => ErrorResponse.From(error),
                SqlRequest request => await ExplainAsync(database, name!, request, cancellationToken),
            };
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return ErrorResponse.From(new RequestTimeout("the EXPLAIN body must arrive within 30 seconds"));
        }
    }

    private async ValueTask<IResult> ExplainAsync(TenantContext database, string name, SqlRequest request, CancellationToken cancellationToken) =>
        SqlSubset.Parse(request.Sql) switch
        {
            Validation error => ErrorResponse.From(error),
            SqlStatement statement => await ExplainValidatedAsync(database, name, statement, cancellationToken),
        };

    private async ValueTask<IResult> ExplainValidatedAsync(TenantContext database, string name, SqlStatement statement, CancellationToken cancellationToken)
    {
        // Body and SQL are valid. Resolve current placement before initializing only the captured identity.
        if (await CheckCurrentAsync(database, name, cancellationToken) is { } changed)
        {
            return changed;
        }

        var initialized = await tenants.EnsureLocalDatabaseAsync(database, cancellationToken);
        if (await CheckCurrentAsync(database, name, cancellationToken) is { } afterInitialization)
        {
            return afterInitialization;
        }

        if (initialized is not Done)
        {
            return initialized switch
            {
                NotFound => ErrorResponse.From(new NotFound()),
                Unavailable error => ErrorResponse.From(error),
                Done => throw new UnreachableException(),
            };
        }

        var check = await queries.CheckAsync(database, statement, cancellationToken);
        if (await CheckCurrentAsync(database, name, cancellationToken) is { } afterPlanning)
        {
            return afterPlanning;
        }

        return check switch
        {
            SqlCheckBody plan => TypedResults.Ok(plan),
            NotFound error => ErrorResponse.From(error),
            Validation error => ErrorResponse.From(error),
        };
    }

    private async ValueTask<IResult?> CheckCurrentAsync(TenantContext database, string name, CancellationToken cancellationToken)
    {
        var found = await tenants.ResolveProjectDatabaseAsync(database.ProjectId!, name, cancellationToken);
        if (found is not ResolvedProjectDatabase current)
        {
            return found switch
            {
                NotFound => ErrorResponse.From(new NotFound()),
                Unavailable error => ErrorResponse.From(error),
                ResolvedProjectDatabase => throw new UnreachableException(),
            };
        }

        if (current.Database.OrganizationId != database.OrganizationId || current.Database.ProjectId != database.ProjectId
            || current.Database.DatabaseId != database.DatabaseId)
        {
            return ErrorResponse.From(new NotFound());
        }

        return !current.Placement.Includes(options.Region)
            ? DatabaseRegions.WrongRegion(current.Placement.Home, current.Placement.Regions)
            : null;
    }

    private static IResult TooLarge() => ErrorResponse.From(new PayloadTooLarge("the body is at most 128 KiB"));
}
