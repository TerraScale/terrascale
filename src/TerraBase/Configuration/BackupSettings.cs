using System.Globalization;
using TerraBase.Tenancy;

namespace TerraBase.Configuration;

/// <summary>
/// Where continuous backup ships to (TS-633, <c>docs/backup.md</c>): the app's Tigris bucket and the master key that wraps
/// each project's data key. On Fly the bucket comes from the variables <c>fly storage create</c> sets and the key from the
/// fly secret <c>TERRABASE_MASTER_KEY</c>. Set the required settings together; none turns backup off.
/// </summary>
public sealed class BackupSettings
{
    /// <summary>Every backup setting.</summary>
    internal static string[] Keys => [.. Required, "backup-retention-days", "backup-key-rotation-days"];

    // Required together; the rest are optional.
    private static readonly string[] Required =
    [
        "backup-endpoint", "backup-region", "backup-bucket", "backup-access-key-id", "backup-secret-key", "master-key",
    ];

    /// <summary>The variables Fly sets for a Tigris bucket attached to the app, and the settings they fill.</summary>
    internal static readonly (string Variable, string Key)[] TigrisVariables =
    [
        ("AWS_ENDPOINT_URL_S3", "backup-endpoint"), ("AWS_REGION", "backup-region"), ("BUCKET_NAME", "backup-bucket"),
        ("AWS_ACCESS_KEY_ID", "backup-access-key-id"), ("AWS_SECRET_ACCESS_KEY", "backup-secret-key"),
    ];

    /// <summary>S3-compatible endpoint, for example <c>https://fly.storage.tigris.dev</c>.</summary>
    public required Uri Endpoint { get; init; }

    /// <summary>Signing region of <see cref="Endpoint"/>; Tigris uses <c>auto</c>.</summary>
    public required string Region { get; init; }

    /// <summary>The bucket every object goes to.</summary>
    public required string Bucket { get; init; }

    /// <summary>Access key id of the S3 credential.</summary>
    public required string AccessKeyId { get; init; }

    /// <summary>The secret of the S3 credential.</summary>
    public required string SecretKey { get; init; }

    /// <summary>The 256-bit master key that wraps every data key (<c>TERRABASE_MASTER_KEY</c>, base64).</summary>
    public required byte[] MasterKey { get; init; }

    /// <summary>Maximum age of a backup data key before the next upload rotates it; 90 days by default.</summary>
    public int KeyRotationDays { get; init; } = 90;

    /// <summary>
    /// Days each plan keeps backups (<c>backup-retention-days</c>, default <c>hobby=1,pro=7,enterprise=30</c>): a database
    /// restores to any time in that window. Plans move to ts-dashboard (TS-599); this stays the fallback.
    /// </summary>
    public IReadOnlyDictionary<PlanTier, int> RetentionDays { get; init; } = DefaultRetentionDays;

    private static readonly Dictionary<PlanTier, int> DefaultRetentionDays = new() { [PlanTier.Hobby] = 1, [PlanTier.Pro] = 7, [PlanTier.Enterprise] = 30 };

    /// <summary>Names the bucket only: the credentials stay out of logs.</summary>
    public override string ToString() => $"backup to {Bucket}";

    /// <summary>Reads the settings, or <see langword="null"/> when none is set.</summary>
    internal static BackupSettings? Parse(IReadOnlyDictionary<string, string> values)
    {
        if (!Keys.Any(values.ContainsKey))
        {
            return null;
        }

        if (!Required.All(values.ContainsKey))
        {
            throw new TerraBaseConfigurationException($"{string.Join(", ", Required)} must be set together");
        }

        return new BackupSettings
        {
            Endpoint = Https(values, "backup-endpoint"),
            Region = Token(values, "backup-region"),
            Bucket = IsBucket(values["backup-bucket"])
                ? values["backup-bucket"]
                : throw new TerraBaseConfigurationException("backup-bucket must be 3 to 63 lowercase letters, digits, dots or hyphens"),
            AccessKeyId = Token(values, "backup-access-key-id"),
            SecretKey = Token(values, "backup-secret-key"),
            MasterKey = ParseMasterKey(values["master-key"]),
            KeyRotationDays = values.TryGetValue("backup-key-rotation-days", out var rotation) ? ParseKeyRotation(rotation) : 90,
            RetentionDays = values.TryGetValue("backup-retention-days", out var retention) ? ParseRetention(retention) : DefaultRetentionDays,
        };
    }

    private static int ParseKeyRotation(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days is >= 1 and <= 3650
            ? days
            : throw new TerraBaseConfigurationException("backup-key-rotation-days must be an integer from 1 to 3650");

    // plan=days pairs, each 1 to 3650; plans not named keep their default.
    private static Dictionary<PlanTier, int> ParseRetention(string value)
    {
        var days = new Dictionary<PlanTier, int>(DefaultRetentionDays);
        foreach (var entry in value.Split(',', StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split('=', 2);
            PlanTier? plan = parts[0] switch { "hobby" => PlanTier.Hobby, "pro" => PlanTier.Pro, "enterprise" => PlanTier.Enterprise, _ => null };
            if (parts.Length != 2 || plan is null || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count is < 1 or > 3650)
            {
                throw new TerraBaseConfigurationException("backup-retention-days must be comma-separated plan=days pairs (hobby, pro, enterprise; 1 to 3650 days)");
            }

            days[plan.Value] = count;
        }

        return days;
    }

    private static Uri Https(IReadOnlyDictionary<string, string> values, string key) =>
        Uri.TryCreate(values[key], UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0
            && uri.PathAndQuery == "/" && uri.Fragment.Length == 0
            ? uri
            : throw new TerraBaseConfigurationException($"{key} must be an https://host[:port] URL");

    private static string Token(IReadOnlyDictionary<string, string> values, string key) =>
        values[key] is { Length: > 0 and <= 512 } value && !value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
            ? value
            : throw new TerraBaseConfigurationException($"{key} must be 1 to 512 characters without whitespace");

    private static bool IsBucket(string bucket) =>
        bucket.Length is >= 3 and <= 63 && bucket.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-');

    private static byte[] ParseMasterKey(string value)
    {
        var key = new byte[33];
        return Convert.TryFromBase64String(value, key, out var written) && written == 32
            ? key[..32]
            : throw new TerraBaseConfigurationException("master-key must be 32 bytes, base64");
    }
}
