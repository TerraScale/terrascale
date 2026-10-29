using System.Text;

namespace TerraScale.Dashboard.Features.Profile
{
    // Browser forms and MCP preserve omitted fields and the same source limits.
    public static class ProfileChanges
    {
        // A null field is omitted and kept; a present name must not be blank, and an empty phone clears it.
        public static Task SaveAsync(AccountService accounts, SessionRecord session, string? name, string? phone, CancellationToken cancellationToken)
        {
            name = name?.Trim();
            if (name is { Length: 0 })
            {
                throw new AccountAccessException("Enter a display name.", 422, "invalid_profile_name");
            }

            if (name is not null && (name.EnumerateRunes().Count() > 128 || name.Any(char.IsControl)))
            {
                throw new AccountAccessException("Use a valid name with 128 characters or fewer.", 422, "invalid_profile_name");
            }

            phone = phone?.Trim();
            if (phone is { Length: > 0 } && !ValidPhone(phone))
            {
                throw new AccountAccessException("Enter a valid phone number.", 422, "invalid_profile_phone");
            }

            return accounts.UpdateAccountAsync(session, account =>
            {
                if (name is not null)
                {
                    account.Name = name;
                }

                if (phone is not null)
                {
                    account.Phone = phone;
                }
            }, cancellationToken);
        }

        // E.164 allows at most 15 digits; separators people type (spaces, dots, dashes, parentheses) are kept as entered.
        private static bool ValidPhone(string phone)
        {
            if (Encoding.UTF8.GetByteCount(phone) > 64)
            {
                return false;
            }

            var digits = 0;
            for (var index = 0; index < phone.Length; index++)
            {
                var character = phone[index];
                if (char.IsAsciiDigit(character))
                {
                    digits++;
                }
                else if (character == '+' ? index != 0 : character is not (' ' or '-' or '.' or '(' or ')'))
                {
                    return false;
                }
            }
            return digits is >= 7 and <= 15;
        }
    }
}
