using System.Runtime.CompilerServices;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Stripe;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.E2E
{
    // This entry point exists only in the TUnit executable. The production
    // dashboard binary has no test-host switch or endpoint/identity override.
    internal static class StripeAuditCliEntryPoint
    {
        [ModuleInitializer]
        internal static void DispatchTestProcess()
        {
            var args = Environment.GetCommandLineArgs();
            var marker = Array.IndexOf(args, "--stripe-audit-test-host");
            if (marker < 0)
            {
                return;
            }

            if (marker + 2 >= args.Length)
            {
                Environment.Exit(2);
            }

            var proxy = args[marker + 1];
            var caPath = args[marker + 2];
            var commandArgs = args.Skip(marker + 3).ToArray();
            try
            {
                var credentials = JsonSerializer.Deserialize<DashboardCredentials>(Console.In.ReadToEnd())
                    ?? throw new InvalidDataException("The test identity was not provided.");
                // The fixture PEM is a public CA certificate, without a private key.
                using var root = X509Certificate2.CreateFromPem(System.IO.File.ReadAllText(caPath));
                var identity = new DashboardIdentity(name => name switch
                {
                    DashboardIdentity.AuthClientSecretName => credentials.AuthClientSecret,
                    DashboardIdentity.SessionEncryptionKeyName => credentials.SessionEncryptionKey,
                    DashboardIdentity.StripeSecretKeyName => credentials.Stripe?.SecretKey,
                    DashboardIdentity.StripeWebhookSecretName => credentials.Stripe?.WebhookSecret,
                    DashboardIdentity.TerraBasePeerSecretName => credentials.TerraBasePeerSecret,
                    _ => null
                });
                StripeClient CreateClient(string key)
                {
                    var handler = new HttpClientHandler
                    {
                        Proxy = new WebProxy(proxy),
                        UseProxy = true,
                        ServerCertificateCustomValidationCallback = (request, certificate, _, _) =>
                        {
                            if (request.RequestUri?.Host != "api.stripe.com" || certificate is null ||
                                !string.Equals(new X509Certificate2(certificate).GetNameInfo(X509NameType.DnsName, false), "api.stripe.com", StringComparison.OrdinalIgnoreCase))
                            {
                                return false;
                            }

                            using var chain = new X509Chain();
                            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                            chain.ChainPolicy.CustomTrustStore.Add(root);
                            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                            return chain.Build(new X509Certificate2(certificate));
                        }
                    };
                    var http = new HttpClient(handler, disposeHandler: true);
                    return new StripeClient(new StripeClientOptions { ApiKey = key, HttpClient = new SystemNetHttpClient(http, enableTelemetry: false) });
                }
                var result = new StripeAuditCommand(identity, CreateClient).RunAsync(commandArgs).GetAwaiter().GetResult();
                Environment.Exit(result);
            }
            catch (Exception)
            {
                Console.Error.WriteLine("Stripe audit test child failed safely.");
                Environment.Exit(2);
            }
        }
    }
}
