using System.Text;
using TerraScale.Dashboard.Errors;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed record ProfileNameInvalid(string Message)
    {
        public const string Code = "invalid_profile_name";
    }

    public sealed record ProfilePhoneInvalid(string Message)
    {
        public const string Code = "invalid_profile_phone";
    }

    public readonly union ProfileSaveResult(Done, ProfileNameInvalid, ProfilePhoneInvalid);

    public static class ProfileChanges
    {
        public static async Task<ProfileSaveResult> SaveAsync(AccountService accounts, SessionRecord session, string? name, string? phone, CancellationToken cancellationToken)
        {
            name = name?.Trim();
            if (name is { Length: 0 })
            {
                return new ProfileNameInvalid("Enter a display name.");
            }

            if (name is not null && (name.EnumerateRunes().Count() > 128 || name.Any(char.IsControl)))
            {
                return new ProfileNameInvalid("Use a valid name with 128 characters or fewer.");
            }

            phone = phone?.Trim();
            if (phone is { Length: > 0 } && !ValidPhone(phone))
            {
                return new ProfilePhoneInvalid("Enter a valid phone number.");
            }

            await accounts.UpdateAccountAsync(session, account =>
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
            return new Done();
        }

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
