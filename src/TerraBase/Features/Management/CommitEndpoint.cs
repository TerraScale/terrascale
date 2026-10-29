using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Features.Qos;
using TerraBase.Features.Catalog;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

public sealed record CommitRequest(string? Id, IReadOnlyList<ManagementOpRequest>? Ops);

public sealed class CommitEndpoint(IManagementCommits commits, TimeProvider time, IManagementService management, IQosAdmission qos, IDocumentCacheQuota cacheQuota, ICatalogGate gate) : EndpointWithoutRequest<IResult>
{
    public const string ReplayedHeader = "TerraBase-Commit-Replayed";

    private const int MaxOps = 1024;
    private const int MaxAssertCounts = 16;
    private const int MaxDatabaseOps = 16;
    private const int MaxLimits = 16;
    private const int MaxLimit = 1_000_000;

    private static readonly Dictionary<string, string[]> Members = new(StringComparer.Ordinal)
    {
        ["create_organization"] = ["name", "id?"],
        ["rename_organization"] = ["organization", "name"],
        ["set_plan"] = ["organization", "plan"],
        ["delete_organization"] = ["organization"],
        ["set_current_organization"] = ["organization"],
        ["create_project"] = ["organization", "name", "id?"],
        ["rename_project"] = ["organization", "project", "name"],
        ["delete_project"] = ["organization", "project"],
        ["create_database"] = ["organization", "project", "name", "id?", "home_region?", "replica_regions?"],
        ["set_database_regions"] = ["organization", "project", "database", "replica_regions"],
        ["branch_database"] = ["organization", "project", "database", "name"],
        ["delete_database"] = ["organization", "project", "database"],
        ["change_role"] = ["organization", "user", "role"],
        ["remove_member"] = ["organization", "user"],
        ["leave"] = ["organization"],
        ["invite"] = ["organization", "email", "token_hash", "role?"],
        ["resend_invitation"] = ["organization", "invitation", "token_hash"],
        ["revoke_invitation"] = ["organization", "invitation"],
        ["accept_invitation"] = ["token_hash"],
        ["decline_invitation"] = ["token_hash"],
        ["start_transfer"] = ["organization", "user"],
        ["confirm_transfer"] = ["organization"],
        ["cancel_transfer"] = ["organization"],
        ["set_audit_retention"] = ["organization", "days"],
        ["fence"] = ["version", "organization?", "account?"],
        ["limit"] = ["organization", "databases?", "members?", "active_members?"],
    };

    public override void Configure()
    {
        Post(ManagementRoute.Commits);
        Name("commit");
        Summary("Applies Management and dashboard document operations atomically");
        Description($$"""
            {"id":"<ULID>","ops":[…]} with 1 to 1024 operations, all committed in one transaction or none (docs/management-commits.md). An operation with a collection member is a Documents API transaction operation on the dashboard document space; any other is a Management operation (snake_case members): create_organization, rename_organization, set_plan, delete_organization, set_current_organization, create_project, rename_project, delete_project, create_database, branch_database, delete_database, change_role, remove_member, leave, invite, resend_invitation, revoke_invitation, accept_invitation, decline_invitation, start_transfer, confirm_transfer, cancel_transfer, set_audit_retention, fence (compare-and-set of an organization's or the account's version) and limit (databases, members or active_members bound; at most 16). Fences and assertions see the state before the commit, other operations run in order, limits compare the counts after with their bound and fail only when the count grew past it. Failures write nothing and name the operation in error.op. A retry with the same id and byte-identical body answers the recorded response with {{ReplayedHeader}}: true; a different body under a committed id is 409 commit_id_reused. Document operations need the internal listener (403 with a ts-auth token). create_project with a client-chosen id also needs the internal listener (403 with a ts-auth token); omit id for a server-generated project id. accept_invitation and decline_invitation use the access token's verified email (403 without one). {{ManagementRoute.IdentityDescription}}
            """);
        Tags(ManagementRoute.Tag);
        Metadata(new RequestBodyMetadata(typeof(CommitRequest), DocumentRoute.JsonMediaType));
        Produces<CommitBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status412PreconditionFailed);
        Produces<ErrorBody>(StatusCodes.Status413PayloadTooLarge);
        Produces<ErrorBody>(StatusCodes.Status415UnsupportedMediaType);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await DocumentRoute.CheckBodyAsync(HttpContext, DocumentRoute.MaxRequestBytes, "invalid_request", cancellationToken, DocumentRoute.JsonMediaType) switch
        {
            Validation error => ErrorResponse.From(error),
            PayloadTooLarge error => ErrorResponse.From(error),
            UnsupportedMediaType error => ErrorResponse.From(error),
            byte[] body => await CommitAsync(body, cancellationToken),
        };

    private async ValueTask<IResult> CommitAsync(byte[] body, CancellationToken cancellationToken)
    {
        var userId = ManagementRoute.UserId(HttpContext);
        JsonDocument envelope;
        try
        {
            envelope = JsonDocument.Parse(body, new JsonDocumentOptions { AllowDuplicateProperties = false });
        }
        catch (JsonException)
        {
            return ErrorResponse.From(new Validation("the body must be valid JSON"));
        }

        using (envelope)
        {
            return Parse(envelope.RootElement, HttpContext.User.IsOperator(), time) switch
            {
                CommitRejected rejected => ErrorResponse.From(rejected.Error, rejected.Op),
                ParsedCommit commit => await ApplyAsync(userId, body, commit, cancellationToken),
            };
        }
    }

    private async ValueTask<IResult> ApplyAsync(string userId, byte[] body, ParsedCommit commit, CancellationToken cancellationToken)
    {
        Tenancy.TenantContext? charged = null;
        foreach (var id in commit.Operations.OfType<ManagementCommitOperation>().Select(op => op.Request.Organization)
            .Where(id => id is not null).Distinct(StringComparer.Ordinal))
        {
            var found = await management.GetOrganizationAsync(userId, id!, cancellationToken).ConfigureAwait(false);
            if (found is Unavailable refused)
            {
                return ErrorResponse.From(refused);
            }

            var organization = found switch
            {
                Organization value => value,
                NotFound => null,
                Unavailable or null => throw new System.Diagnostics.UnreachableException(),
            };
            if (organization is null)
            {
                continue;
            }

            var tenant = new Tenancy.TenantContext(organization.Id, organization.Plan);
            if (qos.TryEnter(tenant, out var retryAfter) is not { } lease)
            {
                HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return ErrorResponse.From(new QuotaExceeded("organization request budget exceeded", "tenant_busy"));
            }

            HttpContext.Response.RegisterForDispose(lease);
            cacheQuota.Reconcile(tenant);
            if (charged is null || tenant.Plan < charged.Plan)
            {
                charged = tenant;
            }
        }

        using var scope = QosContext.Enter(charged);
        if (charged is not null)
        {
            QosContext.ReserveResult(body.LongLength * 3);
        }

        return HttpContext.User.VerifiedEmail() is { } verified
            ? ManagementRoute.CheckEmail(verified) switch
            {
                Validation error => ErrorResponse.From(error),
                string email => await SubmitAsync(userId, email, body, commit, cancellationToken),
                null => throw new System.Diagnostics.UnreachableException(),
            }
            : await SubmitAsync(userId, email: null, body, commit, cancellationToken);
    }

    private async ValueTask<IResult> SubmitAsync(string userId, string? email, byte[] body, ParsedCommit commit, CancellationToken cancellationToken)
    {
        var result = await commits.CommitAsync(userId, email, commit.Id, Convert.ToHexStringLower(SHA256.HashData(body)), commit.Operations, cancellationToken);
        return result switch
        {
            CommitOutcome when commit.Operations.OfType<ManagementCommitOperation>().Any(operation => RevokingOps.Contains(operation.Request.Op ?? string.Empty))
                && !await gate.TryAnchorAsync(cancellationToken) => ErrorResponse.From(CatalogGate.AnchorUnavailable),
            CommitOutcome outcome => Respond(outcome),
            CommitRejected rejected => ErrorResponse.From(rejected.Error, rejected.Op),
        };
    }

    private static readonly FrozenSet<string> RevokingOps = FrozenSet.ToFrozenSet(
    [
        "delete_organization", "delete_project", "delete_database", "change_role", "remove_member", "leave", "revoke_invitation",
        "decline_invitation", "start_transfer", "confirm_transfer", "cancel_transfer", "set_plan", "set_database_regions",
    ], StringComparer.Ordinal);

    private Microsoft.AspNetCore.Http.HttpResults.FileContentHttpResult Respond(CommitOutcome outcome)
    {
        if (outcome.Replayed)
        {
            HttpContext.Response.Headers[ReplayedHeader] = "true";
        }

        return TypedResults.Bytes(outcome.Body, DocumentRoute.JsonMediaType);
    }

    private static ParsedRequest Parse(JsonElement envelope, bool service, TimeProvider time)
    {
        if (envelope.ValueKind != JsonValueKind.Object)
        {
            return new CommitRejected(new Validation("the body must be a JSON object"), Op: null);
        }

        var members = envelope.EnumerateObject().ToDictionary(member => member.Name, member => member.Value, StringComparer.Ordinal);
        if (members.Count != 2
            || !members.TryGetValue("id", out var id) || id.ValueKind != JsonValueKind.String || !IsNewId(id.GetString()!)
            || !members.TryGetValue("ops", out var ops) || ops.ValueKind != JsonValueKind.Array)
        {
            return new CommitRejected(new Validation("the body is {\"id\":\"<canonical ULID>\",\"ops\":[…]} with no other member"), Op: null);
        }

        if (ops.GetArrayLength() is 0 or > MaxOps)
        {
            return new CommitRejected(new Validation("ops must hold 1 to 1024 operations"), Op: null);
        }

        var operations = new List<CommitOperation>(ops.GetArrayLength());
        var targets = new HashSet<(string, string)>();
        var (assertCounts, databaseOps, limits) = (0, 0, 0);
        foreach (var (json, i) in ops.EnumerateArray().Select((json, i) => (json, i)))
        {
            var operation = Operation(json, time) switch
            {
                Validation error => new BoundedOperation(error),
                DocumentCommitOperation document => new BoundedOperation(document),
                ManagementCommitOperation management => new BoundedOperation(management),
            };
            var checkedOperation = operation switch
            {
                Validation error => new BoundedOperation(error),
                Forbidden error => new BoundedOperation(error),
                CommitOperation valid => CheckBounds(valid, service, targets, ref assertCounts, ref databaseOps, ref limits),
            };
            var checkedResult = checkedOperation switch
            {
                Validation error => new ParsedOperation(new CommitRejected(error, i)),
                Forbidden error => new ParsedOperation(new CommitRejected(error, i)),
                CommitOperation valid => new ParsedOperation(valid),
            };
            if (checkedResult is CommitRejected rejected)
            {
                return rejected;
            }

            operations.Add(checkedResult switch
            {
                CommitOperation valid => valid,
                CommitRejected => throw new System.Diagnostics.UnreachableException(),
            });
        }

        return new ParsedCommit(id.GetString()!, operations);
    }

    private static BoundedOperation CheckBounds(
        CommitOperation operation, bool service, HashSet<(string, string)> targets, ref int assertCounts, ref int databaseOps, ref int limits) =>
        operation switch
        {
            DocumentCommitOperation when !service => new Forbidden("document operations need the platform operator"),
            ManagementCommitOperation { Request.Op: "set_plan" } when !service =>
                new Forbidden("plan changes need the platform operator"),
            ManagementCommitOperation { Request: { Op: "create_project", Id: not null } } when !service =>
                new Forbidden("client-chosen project ids need the platform operator"),
            DocumentCommitOperation { Operation: AssertCountOperation } when ++assertCounts > MaxAssertCounts =>
                new Validation("a commit has at most 16 assert_count operations"),
            DocumentCommitOperation { Operation: var document } when TransactionEndpoint.Target(document) is { } target && !targets.Add(target) =>
                new Validation("two operations target the same document"),
            ManagementCommitOperation { Request.Op: "create_database" or "branch_database" or "delete_database" } when ++databaseOps > MaxDatabaseOps =>
                new Validation("a commit has at most 16 database operations"),
            ManagementCommitOperation { Request.Op: "limit" } when ++limits > MaxLimits => new Validation("a commit has at most 16 limit operations"),
            _ => operation,
        };

    private static ValidOperation Operation(JsonElement json, TimeProvider time)
    {
        if (json.ValueKind != JsonValueKind.Object)
        {
            return new Validation("each operation is a JSON object");
        }

        if (json.TryGetProperty("collection", out _))
        {
            return TransactionEndpoint.CheckOperation(json, time) switch
            {
                Validation error => error,
                DocumentOperation operation => new DocumentCommitOperation(operation),
            };
        }

        if (json.EnumerateObject().Any(member => member.Value.ValueKind == JsonValueKind.Null))
        {
            return new Validation("operation members cannot be null");
        }

        return RequestJson.Read<ManagementOpRequest>(JsonMarshal.GetRawUtf8Value(json), "invalid_request") switch
        {
            Validation error => error,
            ManagementOpRequest op => Checked(json, op),
        };
    }

    private static ValidOperation Checked(JsonElement json, ManagementOpRequest op)
    {
        if (op.Op is null || !Members.TryGetValue(op.Op, out var members))
        {
            return new Validation("the operation's kind is invalid");
        }

        var present = json.EnumerateObject().Select(member => member.Name).Where(name => name != "op").ToHashSet(StringComparer.Ordinal);
        if (members.Any(member => !member.EndsWith('?') && !present.Contains(member))
            || present.Any(name => !members.Contains(name) && !members.Contains(name + "?")))
        {
            return new Validation("the operation's members are invalid");
        }

        return CheckMembers(op) switch
        {
            Validation error => error,
            ManagementOpRequest valid => Normalize(valid) switch
            {
                Validation error => error,
                ManagementOpRequest normalized => new ManagementCommitOperation(normalized),
            },
        };
    }

    private static Valid<ManagementOpRequest> CheckMembers(ManagementOpRequest op) =>
        op.Op == "fence" && (op.Organization is null) == (op.Account != true) ? new Validation("a fence names either an organization or account: true")
        : op.Op == "limit" && new[] { op.Databases, op.Members, op.ActiveMembers }.Count(bound => bound is not null) != 1
            ? new Validation("a limit bounds exactly one of databases, members or active_members")
        : !IsIdentifier(op.Organization) ? new Validation("organization must be an id")
        : !IsIdentifier(op.Project) ? new Validation("project must be an id")
        : !IsIdentifier(op.Database) ? new Validation("database must be an id")
        : !IsIdentifier(op.Invitation) ? new Validation("invitation must be an id")
        : op.Id is { } id && !IsNewId(id) ? new Validation("id must be a canonical uppercase ULID")
        : op.HomeRegion is { } home && !TerraBaseOptions.IsRegion(home) ? new Validation("home_region must be a region id")
        : op.ReplicaRegions?.Any(region => region is null || !TerraBaseOptions.IsRegion(region)) == true ? new Validation("replica_regions must be region ids")
        : op.TokenHash is { } hash && !(hash.Length == 64 && hash.All(char.IsAsciiHexDigitLower)) ? new Validation("token_hash must be 64 lowercase hex characters")
        : op.Version is { Length: 0 or > 32 } ? new Validation("version must be 1 to 32 characters")
        : op.Account is false ? new Validation("account is true or absent")
        : op.Days is < 1 or > 90 ? new Validation("days must be between 1 and 90")
        : op.Databases is < 0 or > MaxLimit ? new Validation("databases must be between 0 and 1000000")
        : op.Members is < 0 or > MaxLimit ? new Validation("members must be between 0 and 1000000")
        : op.ActiveMembers is < 0 or > MaxLimit ? new Validation("active_members must be between 0 and 1000000")
        : op;

    private static Valid<ManagementOpRequest> Normalize(ManagementOpRequest op) =>
        (op.User is null ? new Valid<string>(string.Empty) : ManagementRoute.CheckUserId(op.User, "user"),
        op.Name is null ? new Valid<string>(string.Empty)
            : op.Op is "create_database" or "branch_database" ? ManagementRoute.CheckDatabaseName(new NameRequest(op.Name)) : ManagementRoute.CheckDisplayName(new NameRequest(op.Name)),
        op.Plan is null ? new Valid<TerraBase.Tenancy.PlanTier>(TerraBase.Tenancy.PlanTier.Hobby) : ManagementRoute.CheckPlan(new PlanRequest(op.Plan)),
        op.Role is null ? new Valid<OrganizationRole>(OrganizationRole.Member) : ManagementRoute.CheckRole(op.Role),
        op.Email is null ? new Valid<string>(string.Empty) : ManagementRoute.CheckEmail(op.Email)) switch
        {
            (Validation error, _, _, _, _) => error,
            (_, Validation error, _, _, _) => error,
            (_, _, Validation error, _, _) => error,
            (_, _, _, Validation error, _) => error,
            (_, _, _, _, Validation error) => error,
            (string user, string name, TerraBase.Tenancy.PlanTier, OrganizationRole role, string email) => op with
            {
                User = op.User is null ? null : user,
                Name = op.Name is null ? null : name,
                Role = op.Role is null ? null : role,
                Email = op.Email is null ? null : email,
            },
        };

    private static bool IsIdentifier(string? id) => id is null || EntityIds.IsValid(id);

    private static bool IsNewId(string id) => EntityIds.IsValid(id);
}

internal sealed record ParsedCommit(string Id, IReadOnlyList<CommitOperation> Operations);

internal readonly union ParsedRequest(ParsedCommit, CommitRejected);

internal readonly union ValidOperation(DocumentCommitOperation, ManagementCommitOperation, Validation);

internal readonly union BoundedOperation(CommitOperation, Validation, Forbidden);

internal readonly union ParsedOperation(CommitOperation, CommitRejected);
