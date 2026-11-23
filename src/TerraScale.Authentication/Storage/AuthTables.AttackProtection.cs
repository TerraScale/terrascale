using TerraScale.Authentication.Features.AttackProtection;

namespace TerraScale.Authentication.Storage;

public static partial class AuthTables
{
    public static readonly Table<AccountLockRecord> AccountLocks = new(
        "account_locks",
        ["instance_id", "subject"],
        ["instance_id", "subject", "failures", "last_attempt", "locked_until", "unlock_hash", "user_id"],
        static row =>
        [
            row.InstanceId,
            row.Subject,
            row.Failures,
            AuthDb.Instant(row.LastAttempt),
            AuthDb.Instant(row.LockedUntil),
            row.UnlockHash,
            row.UserId,
        ],
        static row => row.Version);

    public static readonly Table<KnownDeviceRecord> KnownDevices = new(
        "known_devices",
        ["instance_id", "user_id", "fingerprint"],
        ["instance_id", "user_id", "fingerprint", "last_seen"],
        static row =>
        [
            row.InstanceId,
            row.UserId,
            row.Fingerprint,
            AuthDb.Instant(row.LastSeen),
        ],
        static row => row.Version);
}
