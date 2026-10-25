using System.Security.Cryptography;
using System.Text;
using TerraBase.Configuration;

namespace TerraBase.Features.Replication;

/// <summary>The shared credential for every request on the internal listener.</summary>
internal sealed class PeerCredential
{
    internal const string EnvironmentVariable = "TERRABASE_PEER_SECRET";
    internal const string HeaderName = "X-TerraBase-Peer-Credential";
    private readonly byte[]? _bytes;

    internal string? CorrelationKey { get; }

    internal PeerCredential(string? value)
    {
        if (value is not null && (value.Length != 64 || !value.All(char.IsAsciiHexDigit)))
        {
            throw new TerraBaseConfigurationException($"{EnvironmentVariable} must contain 64 hexadecimal characters");
        }

        CorrelationKey = value;
        _bytes = value is null ? null : Encoding.ASCII.GetBytes(value);
    }

    internal static PeerCredential FromProcess(TerraBaseOptions options)
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrEmpty(value))
        {
            if (options.PeersFile is not null)
            {
                throw new TerraBaseConfigurationException($"{EnvironmentVariable} is required in a cluster");
            }

            value = null;
        }

        return new PeerCredential(value);
    }

    internal bool Matches(HttpContext context)
    {
        if (_bytes is null || !context.Request.Headers.TryGetValue(HeaderName, out var values)
            || values.Count != 1 || values[0] is not { Length: 64 } value || !value.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(_bytes, Encoding.ASCII.GetBytes(value));
    }

    internal void Authenticate(HttpRequestMessage request)
    {
        if (CorrelationKey is null)
        {
            throw new InvalidOperationException($"{EnvironmentVariable} is required for internal requests");
        }

        request.Headers.Remove(HeaderName);
        request.Headers.Add(HeaderName, CorrelationKey);
    }

    internal HttpMessageHandler Wrap(HttpMessageHandler handler) => new CredentialHandler(this) { InnerHandler = handler };

    private sealed class CredentialHandler(PeerCredential credential) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            credential.Authenticate(request);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
