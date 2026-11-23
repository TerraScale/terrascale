using System.Net;

namespace TerraScale.DotNet.Errors;

public sealed record Done;

public sealed record AuthRefused(HttpStatusCode? Status, string Message, string? ErrorCode = null, TimeSpan? RetryAfter = null, string? RequestId = null);

public readonly union AuthResult<T>(T, AuthRefused);

public readonly union AuthOutcome(Done, AuthRefused);

public sealed record InProgress;

public readonly union AuthCompletion(Done, InProgress, AuthRefused);

public readonly union StepUpConsumed(TerraScaleAuthStepUpGrant, Done, AuthRefused);
