using System.Security.Cryptography;
using System.Text;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Platform.Http
{
    public static class RequestIdentity
    {
        public const string Header = "X-Request-ID";
        public const string ProofHeader = "X-TerraScale-Request-Proof";
        private static readonly AsyncLocal<string?> Current = new();
        public static string? Id => Current.Value;

        public static async Task MiddlewareAsync(HttpContext http, Func<Task> next, ILogger logger)
        {
            // Public caller headers are correlation claims, never authority.
            var supplied = http.Request.Headers[Header];
            var proof = http.Request.Headers[ProofHeader];
            var id = supplied.Count == 1 && proof.Count == 1 && TerraScaleIds.IsValid(supplied[0]) &&
                Verify(supplied[0], proof[0], (await http.RequestServices.GetRequiredService<IDashboardIdentity>().ReadAsync(http.RequestAborted)).TerraBasePeerSecret)
                ? supplied[0]! : AccountService.NewId();
            http.Request.Headers.Remove(ProofHeader);
            var previous = Current.Value;
            Current.Value = id;
            http.TraceIdentifier = id;
            http.Response.OnStarting(() => { http.Response.Headers[Header] = id; http.Response.Headers.Remove(ProofHeader); return Task.CompletedTask; });
            using var scope = logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = id });
            try { await next(); }
            finally { Current.Value = previous; }
        }

        public static void Forward(HttpRequestMessage request, string secret)
        {
            request.Headers.Remove(Header);
            request.Headers.Remove(ProofHeader);
            if (Id is { } id && TerraScaleIds.IsValid(id))
            {
                request.Headers.Add(Header, id);
                request.Headers.Add(ProofHeader, Sign(id, secret));
            }
        }
        public sealed class Handler(IDashboardIdentity identity) : DelegatingHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                request.Headers.Remove(Header);
                request.Headers.Remove(ProofHeader);
                if (Id is not null && request.RequestUri is { Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 } uri &&
                    (uri.Host is "auth.terrascale.tech" or "api.terrascale.tech" || uri.Host.EndsWith(".api.terrascale.tech", StringComparison.Ordinal)))
                {
                    Forward(request, (await identity.ReadAsync(cancellationToken)).TerraBasePeerSecret);
                }

                return await base.SendAsync(request, cancellationToken);
            }
        }

        public static string Sign(string id, string secret)
        {
            return Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes("terrascale.request-id.v1\n" + id)));
        }

        public static bool Verify(string? id, string? proof, string secret)
        {
            return TerraScaleIds.IsValid(id) &&
            proof is { Length: 64 } && proof.All(character => char.IsAsciiDigit(character) || character is >= 'A' and <= 'F') &&
            CryptographicOperations.FixedTimeEquals(Convert.FromHexString(proof), Convert.FromHexString(Sign(id!, secret)));
        }
    }
}
