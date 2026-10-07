using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary>A commit: its id and operations (docs/management-commits.md). Its schema documents the request; the endpoint reads it itself.</summary>
/// <param name="Id">Client-chosen lowercase UUID, the same on every retry of one logical update.</param>
/// <param name="Ops">
/// 1 to 1024 operations: Management operations as described here, and document operations, which have a <c>collection</c>
/// member and the members of a Documents API transaction operation (<see cref="TransactionOpRequest"/>).
/// </param>
public sealed record CommitRequest(string? Id, IReadOnlyList<ManagementOpRequest>? Ops);

/// <summary><c>POST /v1/management/commits</c>: applies Management and dashboard document operations atomically.</summary>
public sealed class CommitEndpoint(IManagementCommits commits) : EndpointWithoutRequest<IResult>
{
    /// <summary>Set on a response answered from the record of an earlier, identical commit.</summary>
    public const string ReplayedHeader = "TerraBase-Commit-Replayed";

    private const int MaxOps = 1024;
    private const int MaxAssertCounts = 16;
    private const int MaxDatabaseOps = 16;
    private const int MaxLimits = 16;
    private const int MaxLimit = 1_000_000;

    // Every Management operation and the members it takes; required ones first, then "?"-suffixed optional ones.
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

    /// <inheritdoc />
    public override void Configure()
    {
        Post(ManagementRoute.Commits);
        Name("commit");
        Summary("Applies Management and dashboard document operations atomically");
        Description($$"""
            {"id":"<uuid>","ops":[…]} with 1 to 1024 operations, all committed in one transaction or none (docs/management-commits.md). An operation with a collection member is a Documents API transaction operation on the dashboard document space; any other is a Management operation (snake_case members): create_organization, rename_organization, set_plan, delete_organization, set_current_organization, create_project, rename_project, delete_project, create_database, branch_database, delete_database, change_role, remove_member, leave, invite, resend_invitation, revoke_invitation, accept_invitation, decline_invitation, start_transfer, confirm_transfer, cancel_transfer, set_audit_retention, fence (compare-and-set of an organization's or the account's version) and limit (databases, members or active_members bound; at most 16). Fences and assertions see the state before the commit, other operations run in order, limits compare the counts after with their bound and fail only when the count grew past it. Failures write nothing and name the operation in error.op. A retry with the same id and byte-identical body answers the recorded response with {{ReplayedHeader}}: true; a different body under a committed id is 409 commit_id_reused. Document operations need the management service credential (403 with a ts-auth token). accept_invitation and decline_invitation use the access token's verified email (403 without one). {{ManagementRoute.IdentityDescription}}
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
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
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
            switch (Parse(envelope.RootElement, HttpContext.User.IsManagementService()))
            {
                case CommitRejected rejected:
                    return ErrorResponse.From(rejected.Error, rejected.Op);
                case ParsedCommit commit:
                    var email = HttpContext.User.VerifiedEmail() is { } verified ? ManagementRoute.Email(verified) : null;
                    return await commits.CommitAsync(userId, email, commit.Id, Convert.ToHexStringLower(SHA256.HashData(body)), commit.Operations, cancellationToken) switch
                    {
                        CommitOutcome outcome => Respond(outcome),
                        CommitRejected rejected => ErrorResponse.From(rejected.Error, rejected.Op),
                    };
                default:
                    throw new UnreachableException();
            }
        }
    }

    private FileContentHttpResult Respond(CommitOutcome outcome)
    {
        if (outcome.Replayed)
        {
            HttpContext.Response.Headers[ReplayedHeader] = "true";
        }

        return TypedResults.Bytes(outcome.Body, DocumentRoute.JsonMediaType);
    }

    // {"id":"<uuid>","ops":[…]} exactly, every operation valid; the first invalid one is named by its index.
    private static ParsedRequest Parse(JsonElement envelope, bool service)
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
            return new CommitRejected(new Validation("the body is {\"id\":\"<lowercase uuid>\",\"ops\":[…]} with no other member"), Op: null);
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
            CommitOperation operation;
            switch (Operation(json))
            {
                case Validation error:
                    return new CommitRejected(error, i);
                case DocumentCommitOperation document:
                    operation = document;
                    break;
                case ManagementCommitOperation management:
                    operation = management;
                    break;
                default:
                    throw new UnreachableException();
            }

            Failure? failure = operation switch
            {
                // The dashboard document space is the dashboard's, not any signed-in user's.
                DocumentCommitOperation when !service => new Forbidden("document operations need the management service credential"),
                DocumentCommitOperation { Operation: AssertCountOperation } when ++assertCounts > MaxAssertCounts =>
                    new Validation("a commit has at most 16 assert_count operations"),
                DocumentCommitOperation { Operation: var document } when TransactionEndpoint.Target(document) is { } target && !targets.Add(target) =>
                    new Validation("two operations target the same document"),
                ManagementCommitOperation { Request.Op: "create_database" or "branch_database" or "delete_database" } when ++databaseOps > MaxDatabaseOps =>
                    new Validation("a commit has at most 16 database operations"),
                ManagementCommitOperation { Request.Op: "limit" } when ++limits > MaxLimits => new Validation("a commit has at most 16 limit operations"),
                _ => null,
            };
            if (failure is { } rejected)
            {
                return new CommitRejected(rejected, i);
            }

            operations.Add(operation);
        }

        return new ParsedCommit(id.GetString()!, operations);
    }

    // One operation; the Management validators still throw (docs/error-handling.md), so their errors are read here.
    private static ValidOperation Operation(JsonElement json)
    {
        try
        {
            return Unchecked(json);
        }
        catch (BadHttpRequestException error)
        {
            return new Validation(error.Message);
        }
    }

    private static ValidOperation Unchecked(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
        {
            return new Validation("each operation is a JSON object");
        }

        if (json.TryGetProperty("collection", out _))
        {
            return TransactionEndpoint.CheckOperation(json) switch
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

        return Invalid(op) is { } error ? error : new ManagementCommitOperation(Normalize(op));
    }

    // The rule a Management operation breaks, beyond those the REST validators in Normalize check.
    private static Validation? Invalid(ManagementOpRequest op) =>
        op.Op == "fence" && (op.Organization is null) == (op.Account != true) ? new Validation("a fence names either an organization or account: true")
        : op.Op == "limit" && new[] { op.Databases, op.Members, op.ActiveMembers }.Count(bound => bound is not null) != 1
            ? new Validation("a limit bounds exactly one of databases, members or active_members")
        : !IsIdentifier(op.Organization) ? new Validation("organization must be an id")
        : !IsIdentifier(op.Project) ? new Validation("project must be an id")
        : !IsIdentifier(op.Database) ? new Validation("database must be an id")
        : !IsIdentifier(op.Invitation) ? new Validation("invitation must be an id")
        : op.Id is { } id && !IsNewId(id) ? new Validation("id must be a lowercase hyphenated UUID")
        : op.HomeRegion is { } home && !TerraBaseOptions.IsRegion(home) ? new Validation("home_region must be a region id")
        : op.ReplicaRegions?.Any(region => region is null || !TerraBaseOptions.IsRegion(region)) == true ? new Validation("replica_regions must be region ids")
        : op.TokenHash is { } hash && !(hash.Length == 64 && hash.All(char.IsAsciiHexDigitLower)) ? new Validation("token_hash must be 64 lowercase hex characters")
        : op.Version is { Length: 0 or > 32 } ? new Validation("version must be 1 to 32 characters")
        : op.Account is false ? new Validation("account is true or absent")
        : op.Days is < 1 or > 90 ? new Validation("days must be between 1 and 90")
        : op.Databases is < 0 or > MaxLimit ? new Validation("databases must be between 0 and 1000000")
        : op.Members is < 0 or > MaxLimit ? new Validation("members must be between 0 and 1000000")
        : op.ActiveMembers is < 0 or > MaxLimit ? new Validation("active_members must be between 0 and 1000000")
        : null;

    // Validates the remaining members as the REST routes do and returns them trimmed.
    private static ManagementOpRequest Normalize(ManagementOpRequest op) => op with
    {
        User = op.User is null ? null : ManagementRoute.ValidUserId(op.User, "user"),
        Name = op.Name is null ? null
            : op.Op is "create_database" or "branch_database" ? ManagementRoute.DatabaseName(new NameRequest(op.Name)) : ManagementRoute.DisplayName(new NameRequest(op.Name)),
        Plan = op.Plan is null ? null : Plan(op.Plan),
        Role = op.Role is null ? null : ManagementRoute.Role(op.Role),
        Email = op.Email is null ? null : ManagementRoute.Email(op.Email),
    };

    private static string Plan(string plan)
    {
        _ = ManagementRoute.Plan(new PlanRequest(plan));
        return plan;
    }

    // Resource ids are opaque here, as in route segments: an unknown one is simply not found.
    private static bool IsIdentifier(string? id) => id is null || (id.Length is > 0 and <= 64 && !id.Any(char.IsControl));

    private static bool IsNewId(string id) => Guid.TryParseExact(id, "D", out var parsed) && parsed.ToString("D") == id;
}

/// <summary>A commit request read and validated: its id and operations.</summary>
/// <param name="Id">The commit id.</param>
/// <param name="Operations">The operations.</param>
internal sealed record ParsedCommit(string Id, IReadOnlyList<CommitOperation> Operations);

/// <summary>A commit request, or the rule it breaks (with the index of the operation that breaks it).</summary>
internal readonly union ParsedRequest(ParsedCommit, CommitRejected);

/// <summary>One commit operation read from the request, or the input rule it breaks.</summary>
internal readonly union ValidOperation(DocumentCommitOperation, ManagementCommitOperation, Validation);
