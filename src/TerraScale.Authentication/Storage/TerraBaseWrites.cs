using System.Data.Common;
using System.Net.WebSockets;
using TerraScale.Authentication.Contracts;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Storage;

internal static class TerraBaseWrites
{
    private const string InvalidResponseCode = "invalid_response";

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
        : AuthData.IsUnavailable(failure) || InvalidResponse(failure) ? new AuthStorageDown(failure)
        : null;

    private static AuthBatchWrite? Sent(Exception failure, GuardedCommand? guarded) =>
        AuthData.IsContention(failure) ? new AuthContended(failure)
        : AuthData.IsConflict(failure) || guarded?.GuardFailed(failure) == true ? new AuthConflict(AuthData.LostConflict)
        : RetrySafeRefusal(failure) || NeverConnected(failure) ? new AuthStorageDown(failure)
        : AuthData.IsUnavailable(failure) || InvalidResponse(failure) ? new AuthOutcomeUnknown(failure)
        : null;

    private static bool RetrySafeRefusal(Exception failure) => failure switch
    {
        TerraBaseException
        {
            ErrorCode: TerraBaseSqlErrorCodes.TenantBusy or TerraBaseSqlErrorCodes.Unavailable or TerraBaseSqlErrorCodes.TransactionRolledBack
                or AuthData.StorageBusy or AuthData.StrongQuorumUnreachable,
        } => true,
        _ when failure.InnerException is { } inner => RetrySafeRefusal(inner),
        _ => false,
    };

    private static bool InvalidResponse(Exception failure) => failure is TerraBaseException { ErrorCode: InvalidResponseCode };

    private static bool NeverConnected(Exception failure) => failure switch
    {
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } and not TerraBaseException => true,
        _ when failure.InnerException is { } inner => NeverConnected(inner),
        _ => false,
    };
}
