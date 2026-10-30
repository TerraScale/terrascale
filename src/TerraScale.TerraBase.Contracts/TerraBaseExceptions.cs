namespace TerraScale.TerraBase.Contracts
{
    /// <summary>A TerraBase refusal received over RPC; <see cref="Fault"/> keeps the public API's status and code.</summary>
    public class TerraBaseException(TerraBaseFault fault, Exception? inner = null) : Exception(fault?.Message, inner)
    {
        /// <summary>The refusal as TerraBase sent it.</summary>
        public TerraBaseFault Fault { get; } = fault ?? throw new ArgumentNullException(nameof(fault));

        /// <summary>The HTTP status of the refusal.</summary>
        public int Status => Fault.Status;

        /// <summary>The stable error code.</summary>
        public string ErrorCode => Fault.Code;

        /// <summary>The failing transaction operation, when the refusal names one.</summary>
        public int? OperationIndex => Fault.OperationIndex;

        /// <summary>The wait before a retry, for 429 and 503.</summary>
        public TimeSpan? RetryAfter => Fault.RetryAfterSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;

        /// <summary>The schema validation issues of a 400.</summary>
        public IReadOnlyList<TerraBaseFaultDetail> Details => Fault.Details ?? [];

        /// <summary>The exception type that matches <paramref name="fault"/>'s status.</summary>
        public static TerraBaseException From(TerraBaseFault fault, Exception? inner = null)
        {
            ArgumentNullException.ThrowIfNull(fault);
            return fault.Status switch
            {
                400 or 413 or 415 => new TerraBaseValidationException(fault, inner),
                401 => new TerraBaseAuthenticationException(fault, inner),
                403 => new TerraBaseForbiddenException(fault, inner),
                404 => new TerraBaseNotFoundException(fault, inner),
                409 => new TerraBaseConflictException(fault, inner),
                412 => new TerraBasePreconditionFailedException(fault, inner),
                421 => new TerraBaseWrongRegionException(fault, inner),
                429 => new TerraBaseRateLimitedException(fault, inner),
                503 => new TerraBaseUnavailableException(fault, inner),
                _ => new TerraBaseException(fault, inner),
            };
        }
    }

    /// <summary>400, 413 or 415: the request is invalid.</summary>
    public sealed class TerraBaseValidationException(TerraBaseFault fault, Exception? inner = null) : TerraBaseException(fault, inner);

    /// <summary>401: the credential is missing, invalid, expired or revoked.</summary>
    public sealed class TerraBaseAuthenticationException(TerraBaseFault fault, Exception? inner = null) : TerraBaseException(fault, inner);

    /// <summary>403: the credential lacks the scope or reach.</summary>
    public sealed class TerraBaseForbiddenException(TerraBaseFault fault, Exception? inner = null) : TerraBaseException(fault, inner);

    /// <summary>404: the resource does not exist.</summary>
    public sealed class TerraBaseNotFoundException(TerraBaseFault fault, Exception? inner = null) : TerraBaseException(fault, inner);

    /// <summary>409: the request conflicts with the current state.</summary>
    public sealed class TerraBaseConflictException(TerraBaseFault fault, Exception? inner = null) : TerraBaseException(fault, inner);

    /// <summary>412: a write precondition failed.</summary>
    public sealed class TerraBasePreconditionFailedException(TerraBaseFault fault, Exception? inner = null) : TerraBaseException(fault, inner);

    /// <summary>429: rate limited; retry after <see cref="TerraBaseException.RetryAfter"/>.</summary>
    public sealed class TerraBaseRateLimitedException(TerraBaseFault fault, Exception? inner = null) : TerraBaseException(fault, inner);

    /// <summary>503: TerraBase cannot answer now, or a write's outcome is unknown.</summary>
    public sealed class TerraBaseUnavailableException(TerraBaseFault fault, Exception? inner = null) : TerraBaseException(fault, inner);

    /// <summary>421: this region does not serve the database; send the call to <see cref="HomeRegion"/> or one of <see cref="Regions"/>.</summary>
    public sealed class TerraBaseWrongRegionException(TerraBaseFault fault, Exception? inner = null) : TerraBaseException(fault, inner)
    {
        /// <summary>The region that answered.</summary>
        public string? Region => Fault.Region;

        /// <summary>The database's home region.</summary>
        public string? HomeRegion => Fault.HomeRegion;

        /// <summary>The database's regions.</summary>
        public IReadOnlyList<string> Regions => Fault.Regions ?? [];
    }
}
