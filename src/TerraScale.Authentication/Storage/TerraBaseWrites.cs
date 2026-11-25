using System.Data.Common;
using System.Net.WebSockets;
using TerraScale.Authentication.Contracts;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Storage;

internal static class TerraBaseWrites
{
    public static async Task<AuthBatchWrite> ExecuteAsync(GuardedCommand guarded, bool uncommitted, CancellationToken cancellationToken)
    {
        int affected;
        try
        {
            affected = await guarded.Command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception failure) when (failure is HttpRequestException or TimeoutException or WebSocketException or ApiException
            && (uncommitted ? Uncommitted(failure) : Sent(failure, guarded)) is { } refusal)
        {
            return refusal;
        }

        return guarded.Lost(affected) ? new AuthConflict(AuthData.LostConflict) : new AuthCompleted();
    }

    public static async Task<AuthBatchWrite> CommitAsync(DbTransaction transaction, CancellationToken cancellationToken)
    {
        try
        {
            await transaction.CommitAsync(cancellationToken);
            return new AuthCompleted();
        }
        catch (Exception failure) when (failure is HttpRequestException or TimeoutException or WebSocketException or ApiException && Sent(failure, null) is { } refusal)
        {
            return refusal;
        }
    }

    private static AuthBatchWrite? Uncommitted(Exception failure) =>
        AuthData.IsContention(failure) ? new AuthContended(failure)
        : AuthData.IsConflict(failure) ? new AuthConflict(AuthData.LostConflict)
        : AuthData.IsUnavailable(failure) ? new AuthStorageDown(failure)
        : null;

    private static AuthBatchWrite? Sent(Exception failure, GuardedCommand? guarded) =>
        AuthData.IsContention(failure) ? new AuthContended(failure)
        : AuthData.IsConflict(failure) || guarded?.GuardFailed(failure) == true ? new AuthConflict(AuthData.LostConflict)
        : AuthData.IsOutcomeUnknown(failure) ? new AuthOutcomeUnknown(failure)
        : AuthData.IsUnavailable(failure) ? Refused(failure) || NeverConnected(failure) ? new AuthStorageDown(failure) : new AuthOutcomeUnknown(failure)
        : null;

    private static bool Refused(Exception failure) => failure switch
    {
        TerraBaseException { ErrorCode: not null } or TerraBaseException { StatusCode: not null } => true,
        _ when failure.InnerException is { } inner => Refused(inner),
        _ => false,
    };

    private static bool NeverConnected(Exception failure) => failure switch
    {
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } and not TerraBaseException => true,
        _ when failure.InnerException is { } inner => NeverConnected(inner),
        _ => false,
    };
}
