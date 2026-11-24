using System.Collections;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;

namespace TerraScale.Platform;

public sealed record PlatformOptions(
    int Port,
    int MetricsPort,
    Uri TerraBaseUrl,
    QuicEndpointOptions? TerraBaseQuic,
    string PeerSecret,
    byte[] MasterKey,
    Uri? AuthIssuer,
    string? AuthAudience,
    string Region,
    string Node,
    Ulid StateDatabase,
    FileStorageOptions? Files,
    ShapeStorageOptions? Shapes)
{
    public const string PeerCredentialHeader = "X-TerraBase-Peer-Credential";

    public static readonly Ulid DefaultStateDatabase = Ulid.Parse("00000000000000000000000007");

    private static readonly string[] SecretFileKeys =
    [
        "TERRABASE_PEER_SECRET",
        "TERRABASE_MASTER_KEY",
        "TERRABASE_FILES_S3_ACCESS_KEY_ID",
        "TERRABASE_FILES_S3_SECRET_ACCESS_KEY",
        "TERRABASE_SHAPES_ACCESS_KEY_ID",
        "TERRABASE_SHAPES_SECRET_ACCESS_KEY",
        "TERRABASE_SHAPES_CDN_API_TOKEN",
    ];

    public static PlatformOptions FromEnvironment(IDictionary environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in environment)
        {
            if (entry.Key is string key && entry.Value is string { Length: > 0 } value)
            {
                values[key] = value;
            }
        }

        ResolveSecretFiles(values);
        var reader = new EnvironmentReader(values);
        var options = new PlatformOptions(
            reader.Port("TS_PLATFORM_PORT", 8082),
            reader.Port("TS_PLATFORM_METRICS_PORT", 9093),
            reader.HttpUri("TS_PLATFORM_TERRABASE_URL", new Uri("http://127.0.0.1:8081/")),
            QuicEndpointOptions.From(reader),
            reader.Hex("TERRABASE_PEER_SECRET", 32),
            reader.SecretsKey("TERRABASE_MASTER_KEY"),
            reader.OptionalHttpUri("TERRABASE_AUTH_ISSUER"),
            reader.Optional("TERRABASE_AUTH_AUDIENCE"),
            reader.Segment("TS_REGION"),
            reader.Segment("TS_NODE"),
            reader.Identifier("TS_PLATFORM_STATE_DATABASE", DefaultStateDatabase),
            FileStorageOptions.From(reader),
            ShapeStorageOptions.From(reader));
        if (options.AuthIssuer is not null && options.AuthAudience is null)
        {
            throw new ArgumentException("TERRABASE_AUTH_AUDIENCE is required with TERRABASE_AUTH_ISSUER.");
        }

        return options;
    }

    private static void ResolveSecretFiles(Dictionary<string, string> values)
    {
        foreach (var key in SecretFileKeys)
        {
            var fileKey = key + "_FILE";
            if (values.ContainsKey(key) || !values.TryGetValue(fileKey, out var path))
            {
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(path).Trim();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new ArgumentException($"{fileKey} must name a readable file: {error.Message}");
            }

            if (text.Length == 0)
            {
                throw new ArgumentException($"{fileKey} must contain a non-empty value.");
            }

            values[key] = text;
        }
    }
}

public sealed record QuicPemFiles(string CertificatePath, string KeyPath, string AuthorityPath);

public sealed record QuicEndpointOptions(IPEndPoint EndPoint, string NodeId, string Region, QuicPemFiles? Pem)
{
    internal static QuicEndpointOptions? From(EnvironmentReader reader)
    {
        if (reader.Optional("TS_PLATFORM_TERRABASE_QUIC") is not { } endpoint)
        {
            return null;
        }

        var parts = endpoint.Split('@');
        if (parts.Length != 2 || !IPEndPoint.TryParse(parts[1], out var address) || address.Port == 0)
        {
            throw new ArgumentException("TS_PLATFORM_TERRABASE_QUIC must be <nodeId>.<region>@<ip>:<port>.");
        }

        var identity = parts[0].Split('.');
        if (identity.Length != 2)
        {
            throw new ArgumentException("TS_PLATFORM_TERRABASE_QUIC must name the TerraBase node as <nodeId>.<region>.");
        }

        string[] pem = ["TS_PLATFORM_QUIC_CERTIFICATE", "TS_PLATFORM_QUIC_KEY", "TS_PLATFORM_QUIC_AUTHORITY"];
        return new QuicEndpointOptions(
            address,
            identity[0],
            identity[1],
            reader.AnyOf(pem) ? new QuicPemFiles(reader.Required(pem[0]), reader.Required(pem[1]), reader.Required(pem[2])) : null);
    }
}

public sealed record FileStorageOptions(Uri Endpoint, string AccessKeyId, string SecretAccessKey)
{
    internal static FileStorageOptions? From(EnvironmentReader reader)
    {
        string[] keys = ["TERRABASE_FILES_S3_ENDPOINT", "TERRABASE_FILES_S3_ACCESS_KEY_ID", "TERRABASE_FILES_S3_SECRET_ACCESS_KEY"];
        if (!reader.AnyOf(keys))
        {
            return null;
        }

        return new FileStorageOptions(reader.StorageUri(keys[0]), reader.Token(keys[1]), reader.Token(keys[2]));
    }
}

public sealed record ShapeStorageOptions(
    Uri Endpoint,
    string Bucket,
    string AccessKeyId,
    string SecretAccessKey,
    Uri CdnBase,
    string CdnZone,
    string CdnApiToken,
    Uri CdnApi)
{
    internal static ShapeStorageOptions? From(EnvironmentReader reader)
    {
        string[] keys =
        [
            "TERRABASE_SHAPES_ENDPOINT", "TERRABASE_SHAPES_BUCKET", "TERRABASE_SHAPES_ACCESS_KEY_ID", "TERRABASE_SHAPES_SECRET_ACCESS_KEY",
            "TERRABASE_SHAPES_CDN_HOST", "TERRABASE_SHAPES_CDN_ZONE", "TERRABASE_SHAPES_CDN_API_TOKEN",
        ];
        string[] enableKeys =
        [
            "TERRABASE_SHAPES_ENDPOINT", "TERRABASE_SHAPES_BUCKET", "TERRABASE_SHAPES_CDN_HOST", "TERRABASE_SHAPES_CDN_ZONE",
        ];
        if (!reader.AnyOf(enableKeys))
        {
            return null;
        }

        var bucket = reader.Required(keys[1]);
        if (bucket.Length is < 3 or > 63 || !bucket.All(static character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
        {
            throw new ArgumentException("TERRABASE_SHAPES_BUCKET must be 3 to 63 lowercase letters, digits or hyphens.");
        }

        var host = reader.Required(keys[4]);
        var cdn = Uri.TryCreate(host.Contains("://", StringComparison.Ordinal) ? host : "https://" + host + "/", UriKind.Absolute, out var parsed)
            ? parsed
            : throw new ArgumentException("TERRABASE_SHAPES_CDN_HOST must be a DNS host name or an absolute URL.");
        return new ShapeStorageOptions(
            reader.StorageUri(keys[0]),
            bucket,
            reader.Token(keys[2]),
            reader.Token(keys[3]),
            cdn.AbsoluteUri.EndsWith('/') ? cdn : new Uri(cdn.AbsoluteUri + "/"),
            reader.Token(keys[5]),
            reader.Token(keys[6]),
            reader.HttpUri("TERRABASE_SHAPES_CDN_API", new Uri("https://api.cloudflare.com/client/v4/")));
    }
}

internal sealed class EnvironmentReader(IReadOnlyDictionary<string, string> values)
{
    public string? Optional(string key) => values.TryGetValue(key, out var value) ? value : null;

    public bool AnyOf(IReadOnlyList<string> keys) => keys.Any(values.ContainsKey);

    public string Required(string key) =>
        Optional(key) ?? throw new ArgumentException($"{key} is required.");

    public string Token(string key)
    {
        var value = Required(key);
        return value.Length is > 0 and <= 512 && !value.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character))
            ? value
            : throw new ArgumentException($"{key} must be 1 to 512 characters without whitespace.");
    }

    public string Segment(string key)
    {
        var value = Required(key);
        return value.Length is > 0 and <= 63 && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? value
            : throw new ArgumentException($"{key} must be 1 to 63 letters, digits, hyphens or underscores.");
    }

    public string Hex(string key, int bytes)
    {
        var value = Required(key);
        return value.Length == bytes * 2 && value.All(char.IsAsciiHexDigit)
            ? value
            : throw new ArgumentException($"{key} must contain {bytes * 2} hexadecimal characters.");
    }

    public byte[] SecretsKey(string key)
    {
        var value = Required(key);
        var master = new byte[33];
        if (value.Length == 64 && value.All(char.IsAsciiHexDigit))
        {
            Convert.FromHexString(value, master, out _, out _);
        }
        else if (!Convert.TryFromBase64String(value, master, out var written) || written != 32)
        {
            throw new ArgumentException($"{key} must be exactly 32 bytes, base64 or 64 hexadecimal characters.");
        }

        var derived = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, master.AsSpan(0, 32), derived, [], "terrascale.platform.secrets.v2"u8);
        CryptographicOperations.ZeroMemory(master);
        return derived;
    }

    public Ulid Identifier(string key, Ulid fallback)
    {
        if (Optional(key) is not { } text)
        {
            return fallback;
        }

        return Ulid.TryParse(text, out var value)
            ? value
            : throw new ArgumentException($"{key} must be a canonical ULID.");
    }

    public int Port(string key, int fallback)
    {
        if (Optional(key) is not { } text)
        {
            return fallback;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
            ? port
            : throw new ArgumentException($"{key} must be a TCP port from 1 to 65535.");
    }

    public Uri HttpUri(string key, Uri fallback) => OptionalHttpUri(key) ?? fallback;

    public Uri? OptionalHttpUri(string key)
    {
        if (Optional(key) is not { } text)
        {
            return null;
        }

        return Uri.TryCreate(text.EndsWith('/') ? text : text + "/", UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.UserInfo.Length == 0
            ? uri
            : throw new ArgumentException($"{key} must be an absolute http or https URL.");
    }

    public Uri StorageUri(string key)
    {
        var uri = OptionalHttpUri(key) ?? throw new ArgumentException($"{key} is required.");
        return uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback
            ? uri
            : throw new ArgumentException($"{key} must use https unless it names a loopback address.");
    }
}
