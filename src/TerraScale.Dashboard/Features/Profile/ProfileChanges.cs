using System.Text;

namespace TerraScale.Dashboard.Features.Profile;

// Browser forms and MCP preserve omitted fields and the same source limits.
public static class ProfileChanges
{
    public static Task SaveAsync(AccountService accounts, SessionRecord session, string name, string? phone, CancellationToken cancellationToken)
    {
        name = name.Trim();
        if (name.EnumerateRunes().Count() > 128 || name.Any(char.IsControl))
            throw new AccountAccessException("Use a valid name with 128 characters or fewer.", 422, "invalid_profile_name");
        phone = phone?.Trim();
        if (phone is not null && (Encoding.UTF8.GetByteCount(phone) > 64 || phone.Any(char.IsControl)))
            throw new AccountAccessException("Enter a valid phone number.", 422, "invalid_profile_phone");
        return accounts.UpdateAccountAsync(session, account =>
        {
            if (name.Length > 0) account.Name = name;
            if (phone is not null) account.Phone = phone;
        }, cancellationToken);
    }
}
