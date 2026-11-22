namespace TerraScale.DotNet.Errors;

using System.Net;

/// <summary>A ts-auth operation the service refused or a response it could not use. Carries the fixed safe diagnostics the refused call produced.</summary>
public sealed record AuthRefused(HttpStatusCode? Status, string Message, string? ErrorCode = null, TimeSpan? RetryAfter = null, string? RequestId = null);

/// <summary>Either an operation's value or its refusal.</summary>
public readonly union AuthResult<T>(T, AuthRefused);

/// <summary>A status-only operation: succeeded without data, or refused.</summary>
public readonly union AuthOutcome(Done, AuthRefused);

/// <summary>The operation is still finishing in the background; repeating the request resumes or re-checks it.</summary>
public sealed record InProgress;

/// <summary>A deletion-style operation: finished, still finishing in the background, or refused.</summary>
public readonly union AuthCompletion(Done, InProgress, AuthRefused);

/// <summary>A consumed step-up proof: the operation grant the account-deletion purpose returns, no grant for the 204 purposes, or refused.</summary>
public readonly union StepUpConsumed(TerraScaleAuthStepUpGrant, Done, AuthRefused);
