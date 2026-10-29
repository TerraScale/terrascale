using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TerraScale.Dashboard.Platform.Http
{
    // Outbound requests to URLs customers enter (alert webhooks, log drains). Only public HTTPS hosts are reachable:
    // the URL is checked when saved, and every connection re-checks the addresses DNS returns at that moment.
    public interface ICustomerDelivery
    {
        Task<int> PostAsync(Uri url, byte[] body, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken cancellationToken);
    }

    public sealed class CustomerDelivery(IHttpClientFactory http) : ICustomerDelivery
    {
        public const string ClientName = "customer";

        public async Task<int> PostAsync(Uri url, byte[] body, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, CustomerUrls.Validate(url.OriginalString)) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new("application/json");
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await http.CreateClient(ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return (int)response.StatusCode;
        }
    }

    public static class CustomerDeliveryRegistration
    {
        public static IServiceCollection AddCustomerDelivery(this IServiceCollection services)
        {
            services.AddHttpClient(CustomerDelivery.ClientName, client => client.Timeout = TimeSpan.FromSeconds(10)).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
                    if (addresses.Length == 0 || !addresses.All(CustomerUrls.IsPublic))
                    {
                        throw new HttpRequestException("The destination resolves to a non-public address.");
                    }

                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try { await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken); return new NetworkStream(socket, ownsSocket: true); }
                    catch { socket.Dispose(); throw; }
                }
            });
            return services.AddSingleton<ICustomerDelivery, CustomerDelivery>();
        }
    }

    public static class CustomerUrls
    {
        public const string Rule = "Enter a public HTTPS URL without credentials or a fragment (up to 2048 characters).";

        public static Uri Validate(string? value)
        {
            var text = value?.Trim() ?? "";
            if (text.Length is 0 or > 2048 || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Host.Length == 0 || uri.IsLoopback ||
                uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
                uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 && !IsPublic(IPAddress.Parse(uri.DnsSafeHost)) ||
                uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
            {
                throw new ArgumentException(Rule);
            }

            return uri;
        }

        public static bool IsPublic(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast))
            {
                return false;
            }

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = address.GetAddressBytes();
                // 0/8, 10/8, 100.64/10 (carrier-grade NAT and the NetBird mesh), 127/8, 169.254/16, 172.16/12, 192.0.0/24, 192.168/16, 198.18/15, multicast and reserved.
                return !(b[0] is 0 or 10 or 127 || b[0] == 100 && b[1] is >= 64 and <= 127 || b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31 ||
                    b[0] == 192 && b[1] == 0 && b[2] == 0 || b[0] == 192 && b[1] == 168 || b[0] == 198 && b[1] is 18 or 19 || b[0] >= 224);
            }
            var bytes = address.GetAddressBytes();
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal || (bytes[0] & 0xfe) == 0xfc ||
                address.IsIPv6Teredo || bytes[0] == 0x20 && bytes[1] == 0x02 || bytes.Take(12).All(item => item == 0));
        }
    }

    // A customer-provided secret at rest: AES-GCM under the dashboard encryption key, bound to its purpose and owner.
    public sealed record SealedSecret(string Nonce, string Ciphertext, string Tag)
    {
        public static SealedSecret Seal(string value, string key, string purpose)
        {
            var plain = Encoding.UTF8.GetBytes(value);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(Convert.FromBase64String(key), 16);
            try { aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes("TerraScale.Dashboard/customer-secret/v1/" + purpose)); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            return new(Convert.ToBase64String(nonce), Convert.ToBase64String(cipher), Convert.ToBase64String(tag));
        }

        public string Open(string key, string purpose)
        {
            var cipher = Convert.FromBase64String(Ciphertext);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(Convert.FromBase64String(key), 16);
            try
            {
                aes.Decrypt(Convert.FromBase64String(Nonce), cipher, Convert.FromBase64String(Tag), plain, Encoding.UTF8.GetBytes("TerraScale.Dashboard/customer-secret/v1/" + purpose));
                return Encoding.UTF8.GetString(plain);
            }
            catch (AuthenticationTagMismatchException) { throw new JsonException("A stored customer secret is invalid."); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }

        public override string ToString()
        {
            return "SealedSecret [REDACTED]";
        }
    }
}
