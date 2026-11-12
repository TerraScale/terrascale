using TerraScale.Authentication.Contracts;

namespace TerraScale.Authentication.Features.Email;

internal static class FactorEnrollmentNotice
{
    internal static Task SendAsync(IEmailSender sender, UserRecord user, CancellationToken cancellationToken) =>
        sender.SendAsync(user.Email, new EmailMessage("New sign-in factor added", "A new sign-in factor was added to your account. If this was not you, reset your password and sign out everywhere now.")
        { Tenant = user.InstanceId }, cancellationToken);
}
