using Dapper;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Agents;

public static class AgentEnrollmentStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Denied = "denied";
    public const string Collected = "collected";
}

public sealed record AgentEnrollmentRecord(string ProjectId, string Id, string UserCodeHash, string Name, IReadOnlyList<string> RequestedScopes, bool AllowSelfSubject,
    string PublicJwk, string Thumbprint, string Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string? UserId = null, string? AgentId = null, string? Kid = null,
    DateTimeOffset? LastPollAt = null, long? LastProofIssuedAt = null)
{
    public string? Version { get; init; }
}

public sealed record StartAgentEnrollmentRequest(string? Name, IReadOnlyList<string>? RequestedScopes, JsonElement? PublicJwk, bool AllowSelfSubject = false);
public sealed record AgentEnrollmentBody(string EnrollmentId, string UserCode, string VerificationUri, string VerificationUriComplete, int ExpiresIn, int Interval);
public sealed record AgentEnrollmentTokenRequest(string? Proof);
public sealed record AgentEnrollmentTokenBody(string AgentId, string Kid);

public readonly union StartAgentEnrollmentResult(AgentEnrollmentBody, AuthValidation, AuthNotFound, AuthConflict);

public readonly union RedeemAgentEnrollmentResult(AgentEnrollmentTokenBody, AuthUnauthorized, AuthValidation);

public sealed record LiveApprover(SessionRecord Session, string SessionVersion, UserRecord User);

public sealed record AgentEnrollmentView(Versioned<AgentEnrollmentRecord> Enrollment, string UserCode, IReadOnlyList<string> Scopes);

public abstract record class AgentDecision;
public sealed record class AgentApproved : AgentDecision;
public sealed record class AgentDenied : AgentDecision;
public sealed record class AgentStepUpRequired(StepUpBody StepUp) : AgentDecision;
public sealed record class AgentDecisionRefused(string Message) : AgentDecision;

public sealed class AgentEnrollments(AuthData data, PlatformDirectory platform, AgentDirectory agents, StepUpProofs proofs, AttemptLimiter limiter, AuthOptions options, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    public const int StartsPerIp = 10;

    public const int StartsPerProject = 300;

    public const int LookupsPerUser = 20;

    public static readonly TimeSpan LookupWindow = TimeSpan.FromMinutes(15);

    public const string NothingAllowed = "This project allows none of what the agent asks for.";

    private const string Expired = "The request expired. Start again from the agent.";

    private const string Changed = "The request changed. Open the link from the agent again.";

    private const string Alphabet = "BCDFGHJKLMNPQRSTVWXZ";

    private readonly byte[] confirmationKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, options.MasterKey, 32, info: "ts-auth agent enrollment confirmation"u8.ToArray());

    public static string PagePath(string project) => $"/ui/{project}/agent-enrollments";

    public static string? Normalize(string? text)
    {
        var code = string.Concat((text ?? string.Empty).Where(character => character is not ('-' or ' '))).ToUpperInvariant();
        return code.Length == 8 && code.All(Alphabet.Contains) ? code : null;
    }

    public static string Display(string code) => code[..4] + "-" + code[4..];

    public async Task<StartAgentEnrollmentResult> StartAsync(string project, string ip, StartAgentEnrollmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = PlatformDirectory.Name(request.Name);
        if (name is not { Length: <= 64 } || request.RequestedScopes is not { } scopes || !MachineClients.ValidScopes(scopes))
        {
            return new AuthValidation("Supply a name of 1 to 64 characters, 1 to 64 unique requested_scopes and a public_jwk.", "invalid_request");
        }

        var key = AgentJwk.Parse(request.PublicJwk);
        var tenant = Tenancy.IsProject(project) ? await platform.ProjectAsync(project, cancellationToken) : null;
        if (Tenancy.IsProject(project) && tenant is null)
        {
            return new AuthNotFound("No such resource.");
        }

        if ((await limiter.AcquireDurableAsync(project, "agent-enrollment-ip", limiter.Fold(ip), StartsPerIp, LookupWindow, cancellationToken)
            ?? await limiter.AcquireDurableAsync(project, "agent-enrollment", project, StartsPerProject, TimeSpan.FromHours(1), cancellationToken)) is { } wait)
        {
            throw ApiException.RateLimited("Too many agent enrollments started.", wait);
        }

        var code = RandomNumberGenerator.GetString(Alphabet, 8);
        var now = clock.GetUtcNow();
        var enrollment = new AgentEnrollmentRecord(project, AuthIds.New(), Tokens.Sha256Hex(code), name, scopes, request.AllowSelfSubject, key.Jwk, key.Thumbprint,
            AgentEnrollmentStatus.Pending, now, now + Lifetime);
        if (!await data.SaveAsync([Change.Insert(enrollment), .. tenant is null ? [] : new[] { Change.Assert(tenant) }], cancellationToken))
        {
            return new AuthConflict("The enrollment could not be stored; start it again.", "version_conflict");
        }

        var page = new Uri(options.PublicOrigin, PagePath(project)).AbsoluteUri;
        return new AgentEnrollmentBody(enrollment.Id, Display(code), page, page + "?user_code=" + Display(code), (int)Lifetime.TotalSeconds, (int)Interval.TotalSeconds);
    }

    public async Task<AgentEnrollmentView?> FindAsync(string project, string code, CancellationToken cancellationToken)
    {
        var hash = Tokens.Sha256Hex(code);
        var enrollment = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<AgentEnrollmentRecord>(
            "SELECT * FROM agent_enrollments WHERE project_id = @project AND user_code_hash = @hash", new { project, hash }), cancellationToken));
        if (enrollment is not { Value: { Status: AgentEnrollmentStatus.Pending } pending } || pending.ExpiresAt <= clock.GetUtcNow())
        {
            return null;
        }

        var policy = await agents.PolicyAsync(project, cancellationToken);
        return new(enrollment, Display(code), [.. pending.RequestedScopes.Intersect(policy.AllowedScopes, StringComparer.Ordinal)]);
    }

    public string Confirmation(AgentEnrollmentView view, string userId)
    {
        ArgumentNullException.ThrowIfNull(view);
        var enrollment = view.Enrollment.Value;
        string[] bound = [enrollment.ProjectId, enrollment.Id, userId, .. view.Scopes.Order(StringComparer.Ordinal)];
        return Base64Url.EncodeToString(HMACSHA256.HashData(this.confirmationKey, Encoding.UTF8.GetBytes(string.Join('\n', bound))));
    }

    public async Task<AgentDecision> ApproveAsync(LiveApprover approver, AgentEnrollmentView view, string confirmation, string? stepUpProof, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(confirmation);
        var enrollment = view.Enrollment.Value;
        if (view.Scopes.Count == 0)
        {
            return new AgentDecisionRefused(NothingAllowed);
        }

        if (!Tokens.FixedTimeEquals(this.Confirmation(view, approver.User.Id), confirmation))
        {
            return new AgentDecisionRefused("The request changed. Check it again before you approve.");
        }

        if (await proofs.PrepareAsync(approver.User, stepUpProof, StepUpProofs.ApproveAgent, "ts-auth", null, cancellationToken, enrollment.Id) is not { } stepUp)
        {
            return new AgentStepUpRequired(await proofs.RequestAgentApprovalAsync(approver.User, enrollment.Id, PagePath(enrollment.ProjectId) + "?user_code=" + view.UserCode,
                cancellationToken));
        }

        var deadline = new[] { enrollment.ExpiresAt, stepUp.Record.ExpiresAt, approver.Session.ExpiresAt }.Min();
        if (deadline <= clock.GetUtcNow())
        {
            return new AgentDecisionRefused(Expired);
        }

        using var publicJwk = JsonDocument.Parse(enrollment.PublicJwk);
        try
        {
            await agents.CreateAsync(enrollment.ProjectId, approver.User.Id, approver.User.Id,
                new CreateAgentRequest(enrollment.Name, view.Scopes, publicJwk.RootElement.Clone(), enrollment.AllowSelfSubject),
                key => [Change.Update(view.Enrollment, enrollment with
                    {
                        Status = AgentEnrollmentStatus.Approved, UserId = approver.User.Id, AgentId = key.AgentId, Kid = key.Kid, ExpiresAt = clock.GetUtcNow() + Lifetime,
                    }),
                    .. stepUp.Operations, Sessions.Live(approver.Session, approver.SessionVersion, clock.GetUtcNow())], deadline, cancellationToken);
        }
        catch (ApiException refused) when (refused.Status is 400 or 401 or 404 or 409 or 429)
        {
            return new AgentDecisionRefused(refused.Code switch
            {
                "agent_limit" => "You already have the most agents allowed. Revoke one first.",
                "agent_key_in_use" => "This key is already registered. Start again with a new key.",
                "approval_expired" => Expired,
                _ => Changed,
            });
        }

        return new AgentApproved();
    }

    public async Task<AgentDecision> DenyAsync(LiveApprover approver, AgentEnrollmentView view, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentNullException.ThrowIfNull(view);
        var enrollment = view.Enrollment.Value;
        var now = clock.GetUtcNow();
        if (enrollment.ExpiresAt <= now || approver.Session.Revoked || approver.Session.ExpiresAt <= now)
        {
            return new AgentDecisionRefused(Expired);
        }

        return await data.SaveAsync([Change.Update(view.Enrollment, enrollment with { Status = AgentEnrollmentStatus.Denied, UserId = approver.User.Id }),
                Change.Assert(approver.User), Change.Assert(approver.Session, approver.SessionVersion)], cancellationToken)
            ? new AgentDenied()
            : new AgentDecisionRefused(Changed);
    }

    public async Task<RedeemAgentEnrollmentResult> RedeemAsync(string project, string enrollmentId, string? proof, CancellationToken cancellationToken)
    {
        var current = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<AgentEnrollmentRecord>(
            "SELECT * FROM agent_enrollments WHERE project_id = @project AND id = @enrollmentId", new { project, enrollmentId }), cancellationToken));
        var now = clock.GetUtcNow();
        var audience = new Uri(options.PublicOrigin, $"/v1/{project}/agent-enrollments/{enrollmentId}/token").AbsoluteUri;
        if (current is not { Value: var enrollment }
            || await AgentProof.IssuedAtAsync(proof, enrollment.PublicJwk, audience, now) is not { } issuedAt
            || issuedAt <= enrollment.LastProofIssuedAt)
        {
            return new AuthUnauthorized("Sign a fresh ES256 JWT with the enrolled key: aud this URL, a jti, iat, and exp at most 300 seconds ahead.", "invalid_proof");
        }

        if (enrollment.Status == AgentEnrollmentStatus.Collected || enrollment.ExpiresAt <= now)
        {
            return Answer("expired_token", "The enrollment expired or its result was already collected. Start again.");
        }

        if (enrollment.Status == AgentEnrollmentStatus.Denied)
        {
            return Answer("access_denied", "The user denied the agent.");
        }

        if (enrollment.LastPollAt > now - Interval)
        {
            return Answer("slow_down", $"Poll at most every {Interval.TotalSeconds:0} seconds.");
        }

        var approved = enrollment.Status == AgentEnrollmentStatus.Approved;
        var polled = enrollment with { Status = approved ? AgentEnrollmentStatus.Collected : enrollment.Status, LastPollAt = now, LastProofIssuedAt = issuedAt };
        if (!await data.SaveAsync([Change.Update(current, polled)], cancellationToken))
        {
            return Answer("slow_down", $"Poll at most every {Interval.TotalSeconds:0} seconds.");
        }

        return approved
            ? new AgentEnrollmentTokenBody(enrollment.AgentId ?? throw new InvalidDataException("An approved enrollment has no agent."), enrollment.Kid ?? throw new InvalidDataException("An approved enrollment has no key."))
            : Answer("authorization_pending", "The user has not decided yet.");
    }

    public async Task<int> PruneAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        var cutoff = clock.GetUtcNow() - Lifetime;
        return await data.InBatchesAsync(batchSize, connection => connection.ExecuteAsync(
            """
            DELETE FROM agent_enrollments WHERE (project_id, id) IN (
                SELECT project_id, id FROM agent_enrollments WHERE expires_at <= @cutoff ORDER BY expires_at LIMIT @batchSize)
            """, new { cutoff, batchSize }), cancellationToken);
    }

    private static AuthValidation Answer(string code, string message) => new(message, code);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(StartAgentEnrollmentRequest))]
[JsonSerializable(typeof(AgentEnrollmentBody))]
[JsonSerializable(typeof(AgentEnrollmentTokenRequest))]
[JsonSerializable(typeof(AgentEnrollmentTokenBody))]
internal sealed partial class AgentEnrollmentJson : JsonSerializerContext;
