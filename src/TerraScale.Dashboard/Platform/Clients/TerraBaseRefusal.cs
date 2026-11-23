using System.Net;

namespace TerraScale.Dashboard.Platform.Clients;

public sealed record TerraBaseRefusal(int Status, string Code, string Message)
{
    public TimeSpan? RetryAfter { get; init; }

    public string? SqlState { get; init; }

    public int? Statement { get; init; }

    public IReadOnlyDictionary<string, string>? Details { get; init; }

    public static TerraBaseRefusal Unreachable(string message) => new(StatusCodes.Status503ServiceUnavailable, "unavailable", message);

    public static TerraBaseRefusal Invalid(string message) => new(StatusCodes.Status502BadGateway, "invalid_response", message);

    public TerraBaseException Exception() => Status switch
    {
        400 or 413 or 415 => new TerraBaseValidationException(this),
        401 => new TerraBaseAuthenticationException(this),
        403 => new TerraBaseForbiddenException(this),
        404 => new TerraBaseNotFoundException(this),
        409 => new TerraBaseConflictException(this),
        412 => new TerraBasePreconditionFailedException(this),
        421 => new TerraBaseWrongRegionException(this),
        429 => new TerraBaseRateLimitedException(this),
        503 => new TerraBaseUnavailableException(this),
        _ => new TerraBaseException(this),
    };
}

public class TerraBaseException(TerraBaseRefusal refusal) : HttpRequestException(refusal.Message, null, (HttpStatusCode)refusal.Status)
{
    public TerraBaseRefusal Fault { get; } = refusal;

    public int Status => Fault.Status;

    public string ErrorCode => Fault.Code;

    public int? OperationIndex => Fault.Statement;

    public TimeSpan? RetryAfter => Fault.RetryAfter;

    public IReadOnlyDictionary<string, string> Details => Fault.Details ?? new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed class TerraBaseValidationException(TerraBaseRefusal refusal) : TerraBaseException(refusal);

public sealed class TerraBaseAuthenticationException(TerraBaseRefusal refusal) : TerraBaseException(refusal);

public sealed class TerraBaseForbiddenException(TerraBaseRefusal refusal) : TerraBaseException(refusal);

public sealed class TerraBaseNotFoundException(TerraBaseRefusal refusal) : TerraBaseException(refusal);

public sealed class TerraBaseConflictException(TerraBaseRefusal refusal) : TerraBaseException(refusal);

public sealed class TerraBasePreconditionFailedException(TerraBaseRefusal refusal) : TerraBaseException(refusal);

public sealed class TerraBaseRateLimitedException(TerraBaseRefusal refusal) : TerraBaseException(refusal);

public sealed class TerraBaseUnavailableException(TerraBaseRefusal refusal) : TerraBaseException(refusal);

public sealed class TerraBaseWrongRegionException(TerraBaseRefusal refusal) : TerraBaseException(refusal)
{
    public string? Region => Fault.Details?.GetValueOrDefault("region");

    public string? HomeRegion => Fault.Details?.GetValueOrDefault("homeRegion");

    public IReadOnlyList<string> Regions => Fault.Details?.GetValueOrDefault("regions") is { Length: > 0 } regions ? regions.Split(',') : [];
}
