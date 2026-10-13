using System.Text;

namespace TerraScale.Dashboard.Features.Auth;

public interface IProfileIdentityGateway
{
    Task StartPasswordChangeAsync(SessionRecord session, string currentPassword, string ip, CancellationToken cancellationToken);
    Task CompletePasswordChangeAsync(SessionRecord session, string code, string newPassword, string confirmPassword, string ip, CancellationToken cancellationToken);
}

// ts-auth serves no change-own-password route. A signed-in change is its
// password reset for the session's own email, gated on the current password;
// completing it signs the user out of every session.
public sealed partial class TsAuthGateway : IProfileIdentityGateway
{
    public async Task StartPasswordChangeAsync(SessionRecord session, string currentPassword, string ip, CancellationToken cancellationToken)
    {
        SessionRecord fresh;
        try { fresh = await PasswordAsync(session.Email, currentPassword, ip, cancellationToken); }
        catch (SignInRejectedException) { throw new AuthRequestRejectedException("Current password is incorrect.", "currentPassword"); }
        try
        {
            if (fresh.UserId != session.UserId || fresh.Email != session.Email)
                throw new AuthRequestRejectedException("Current password is incorrect.", "currentPassword");
            await StartPasswordResetAsync(session.Email, ip, cancellationToken);
        }
        finally
        {
            // The proof grant is never persisted. Revoke its token family even
            // when the reset start fails or the browser disconnects.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await RevokeAsync(fresh, cleanup.Token); }
            catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException or OperationCanceledException) { }
        }
    }

    public async Task CompletePasswordChangeAsync(SessionRecord session, string code, string newPassword, string confirmPassword, string ip, CancellationToken cancellationToken)
    {
        if (newPassword.EnumerateRunes().Count() < 12)
            throw new AuthRequestRejectedException("Use at least 12 characters.", "newPassword");
        if (Encoding.UTF8.GetByteCount(newPassword) > 1024)
            throw new AuthRequestRejectedException("Use a password with 1024 bytes or fewer.", "newPassword");
        if (newPassword != confirmPassword)
            throw new AuthRequestRejectedException("Passwords do not match.", "confirmPassword");
        try { await CompletePasswordResetAsync(session.Email, code, newPassword, ip, cancellationToken); }
        catch (SignInRejectedException) { throw new AuthRequestRejectedException("The code is incorrect or has expired.", "code"); }
    }
}
