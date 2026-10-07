using System.Security.Cryptography;

namespace TerraBase.Configuration;

/// <summary>
/// Where continuous backup ships to (TS-633, <c>docs/backup.md</c>): an S3-compatible bucket (OCI Object Storage) and the
/// OCI Vault master key that wraps each project's data key. Set all <c>backup-*</c> settings or none; none turns backup off.
/// </summary>
public sealed class BackupSettings
{
    /// <summary>Every backup setting; all are required together.</summary>
    internal static readonly string[] Keys =
    [
        "backup-endpoint", "backup-region", "backup-bucket", "backup-access-key-id", "backup-secret-key-file",
        "backup-kms-endpoint", "backup-kms-key-id", "backup-kms-api-key", "backup-kms-private-key-file",
    ];

    /// <summary>S3-compatible endpoint, for example <c>https://&lt;namespace&gt;.compat.objectstorage.sa-saopaulo-1.oraclecloud.com</c>.</summary>
    public required Uri Endpoint { get; init; }

    /// <summary>Signing region of <see cref="Endpoint"/>, for example <c>sa-saopaulo-1</c>.</summary>
    public required string Region { get; init; }

    /// <summary>The bucket every object goes to.</summary>
    public required string Bucket { get; init; }

    /// <summary>Access key id of the S3 credential (an OCI customer secret key).</summary>
    public required string AccessKeyId { get; init; }

    /// <summary>The secret of the S3 credential.</summary>
    public required string SecretKey { get; init; }

    /// <summary>Cryptographic endpoint of the OCI Vault that holds <see cref="KmsKeyId"/>.</summary>
    public required Uri KmsEndpoint { get; init; }

    /// <summary>OCID of the master encryption key that wraps the data keys.</summary>
    public required string KmsKeyId { get; init; }

    /// <summary>OCI API signing key id: <c>&lt;tenancy OCID&gt;/&lt;user OCID&gt;/&lt;key fingerprint&gt;</c>.</summary>
    public required string KmsApiKey { get; init; }

    /// <summary>PEM private key of <see cref="KmsApiKey"/>.</summary>
    public required string KmsPrivateKeyPem { get; init; }

    /// <summary>Names the bucket only: the credentials stay out of logs.</summary>
    public override string ToString() => $"backup to {Bucket}";

    /// <summary>Reads the settings, or <see langword="null"/> when none is set.</summary>
    internal static BackupSettings? Parse(IReadOnlyDictionary<string, string> values)
    {
        if (!Keys.Any(values.ContainsKey))
        {
            return null;
        }

        if (!Keys.All(values.ContainsKey))
        {
            throw new TerraBaseConfigurationException($"{string.Join(", ", Keys)} must be set together");
        }

        return new BackupSettings
        {
            Endpoint = Https(values, "backup-endpoint"),
            Region = Token(values, "backup-region"),
            Bucket = IsBucket(values["backup-bucket"])
                ? values["backup-bucket"]
                : throw new TerraBaseConfigurationException("backup-bucket must be 3 to 63 lowercase letters, digits, dots or hyphens"),
            AccessKeyId = Token(values, "backup-access-key-id"),
            SecretKey = ReadSecret(values["backup-secret-key-file"], "backup-secret-key-file"),
            KmsEndpoint = Https(values, "backup-kms-endpoint"),
            KmsKeyId = Token(values, "backup-kms-key-id"),
            KmsApiKey = values["backup-kms-api-key"].Split('/') is [{ Length: > 0 }, { Length: > 0 }, { Length: > 0 }] && !values["backup-kms-api-key"].Any(char.IsWhiteSpace)
                ? values["backup-kms-api-key"]
                : throw new TerraBaseConfigurationException("backup-kms-api-key must be <tenancy OCID>/<user OCID>/<fingerprint>"),
            KmsPrivateKeyPem = ReadPrivateKey(values["backup-kms-private-key-file"]),
        };
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

    // The file holds the secret alone; surrounding whitespace (a trailing newline) is ignored.
    private static string ReadSecret(string path, string key)
    {
        try
        {
            return File.ReadAllText(path).Trim() is { Length: > 0 } secret
                ? secret
                : throw new TerraBaseConfigurationException($"{key} must not be empty");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new TerraBaseConfigurationException($"{key} must name a readable file");
        }
    }

    private static string ReadPrivateKey(string path)
    {
        var pem = ReadSecret(path, "backup-kms-private-key-file");
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return pem;
        }
        catch (Exception error) when (error is ArgumentException or CryptographicException)
        {
            throw new TerraBaseConfigurationException("backup-kms-private-key-file must hold a PEM RSA private key");
        }
    }
}
