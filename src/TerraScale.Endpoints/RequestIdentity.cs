using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace TerraScale.Endpoints;

/// <summary>Customer correlation labels authenticated between first-party services, never authorization.</summary>
public static class RequestIdentity
{
    /// <summary>The public response header.</summary>
    public const string Header = "X-Request-ID";
    /// <summary>Internal proof; never return it to clients.</summary>
    public const string ProofHeader = "X-TerraScale-Request-Proof";
    private static readonly AsyncLocal<string?> Current = new();

    /// <summary>Accepts only canonical ULIDs.</summary>
    public static bool IsValid(string? value) => value is { Length: 26 } && value[0] is >= '0' and <= '7'
        && value.All(character => "0123456789ABCDEFGHJKMNPQRSTVWXYZ".Contains(character, StringComparison.Ordinal));

    /// <summary>Signs using the literal Vault credential, with a purpose distinct from authentication.</summary>
    public static string Sign(string id, string secret) => Convert.ToHexString(HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes("terrascale.request-id.v1\n" + id)));

    /// <summary>Assigns identity before errors, routing and authentication; binds structured logging scopes.</summary>
    public static WebApplication UseTerraScaleRequestIdentity(this WebApplication app, Func<string> newId, string? secret)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(newId);
        app.Use(async (context, next) =>
        {
            var ids = context.Request.Headers[Header];
            var proofs = context.Request.Headers[ProofHeader];
            var id = ids.Count == 1 ? ids[0] : null;
            var proof = proofs.Count == 1 ? proofs[0] : null;
            var trusted = IsValid(id) && secret is { Length: > 0 } && proof is { Length: 64 }
                && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(id!, secret)), Encoding.ASCII.GetBytes(proof));
            id = trusted ? id! : newId();
            context.Request.Headers.Remove(ProofHeader);
            context.Request.Headers[Header] = id;
            context.TraceIdentifier = id;
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[Header] = id;
                context.Response.Headers.Remove(ProofHeader);
                return Task.CompletedTask;
            });
            var previous = Current.Value;
            Current.Value = id;
            using var scope = app.Logger.BeginScope(new Dictionary<string, object> { ["request_id"] = id });
            try
            { await next(context).ConfigureAwait(false); }
            finally { Current.Value = previous; }
        });
        return app;
    }

    /// <summary>Install only on first-party transports; never on webhooks or third-party HTTP clients.</summary>
    public static HttpMessageHandler Wrap(HttpMessageHandler inner, string? secret) => new Propagation(secret) { InnerHandler = inner };

    private sealed class Propagation(string? secret) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Remove(Header);
            request.Headers.Remove(ProofHeader);
            if (Current.Value is { } id && secret is { Length: > 0 })
            {
                request.Headers.Add(Header, id);
                request.Headers.Add(ProofHeader, Sign(id, secret));
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}
