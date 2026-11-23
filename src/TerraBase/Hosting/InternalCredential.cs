using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using TerraBase.Configuration;

namespace TerraBase.Hosting;

internal readonly union InternalCredentialOutcome(InternalCredential, ConfigurationError);

internal sealed class InternalCredential
{
    public const string EnvironmentVariable = "TERRABASE_PEER_SECRET";

    public const string HeaderName = "X-TerraBase-Peer-Credential";

    private readonly byte[]? _bytes;

    private InternalCredential(string? value)
    {
        CorrelationKey = value;
        _bytes = value is null ? null : Encoding.ASCII.GetBytes(value);
    }

    public string? CorrelationKey { get; }

    public static InternalCredentialOutcome FromProcess(TerraBaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrEmpty(value))
        {
            return options.PeersFile is not null
                ? new ConfigurationError(EnvironmentVariable + " is required in a cluster")
                : new InternalCredential(null);
        }

        return value.Length == 64 && value.All(char.IsAsciiHexDigit)
            ? new InternalCredential(value)
            : new ConfigurationError(EnvironmentVariable + " must contain 64 hexadecimal characters");
    }

    public bool Matches(HttpContext context)
    {
        if (_bytes is null || !context.Request.Headers.TryGetValue(HeaderName, out var values)
            || values.Count != 1 || values[0] is not { Length: 64 } value || !value.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(_bytes, Encoding.ASCII.GetBytes(value));
    }

    public void Sign(HttpRequestHeaders headers)
    {
        if (CorrelationKey is not null)
        {
            headers.TryAddWithoutValidation(HeaderName, CorrelationKey);
        }
    }
}
