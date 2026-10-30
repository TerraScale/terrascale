using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.E2E.Features.Auth
{
    public sealed class RefreshAuthorityJourneyTests
    {
        [Test]
        public async Task RefreshOverlapsPublicReadsAndUserinfoWithoutReusingIdentityAcrossPages()
        {
            using var wire = new Wire();
            var gateway = new TsAuthGateway(wire, wire, TimeProvider.System, new TerraScale.Dashboard.Platform.TsAuth.TsAuthClient(wire.CreateClient("platform")));
            SessionRecord refreshed = Session();
            await Page(async () =>
            {
                var refresh = gateway.RefreshAsync(Session(), CancellationToken.None);
                await wire.TokenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.That(wire.Metadata).IsEqualTo(1);
                await Assert.That(wire.Keys).IsEqualTo(1);
                wire.TokenRelease.SetResult();
                await wire.UserinfoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.That(refresh.IsCompleted).IsFalse();
                wire.PublicRelease.SetResult();
                refreshed = await refresh;
                await Assert.That(refreshed.RefreshLease).IsEqualTo("");
                await Assert.That(refreshed.RefreshLeaseUntil).IsNull();
                await Assert.That(await gateway.IsActiveAsync(refreshed, CancellationToken.None)).IsTrue();
                await Assert.That(wire.Users).IsEqualTo(1);
                await Assert.That(await gateway.IsActiveAsync(refreshed with { UserId = "other" }, CancellationToken.None)).IsFalse();
                await Assert.That(await gateway.IsActiveAsync(refreshed with { Email = "other@example.test" }, CancellationToken.None)).IsFalse();
                await gateway.IsActiveAsync(refreshed with { AccessToken = "other" }, CancellationToken.None);
                await Assert.That(wire.Users).IsEqualTo(4);
                using var client = wire.CreateClient("mutation");
                using var mutation = await client.PostAsync("https://auth.terrascale.tech/v1/membership", null);
                await gateway.IsActiveAsync(refreshed, CancellationToken.None);
                await Assert.That(wire.Users).IsEqualTo(5);
                // Account writes invalidate identity, but public signing material is independent.
                refreshed = await gateway.RefreshAsync(refreshed, CancellationToken.None);
                await Assert.That(wire.Metadata).IsEqualTo(1);
                await Assert.That(wire.Keys).IsEqualTo(1);
                await Assert.That(wire.Users).IsEqualTo(5);
            });
            wire.RotateKey();
            await Page(async () =>
            {
                refreshed = await gateway.RefreshAsync(refreshed, CancellationToken.None);
                await Assert.That(wire.Metadata).IsEqualTo(2);
                await Assert.That(wire.Keys).IsEqualTo(2);
                await Assert.That(wire.Users).IsEqualTo(6);
            });
            wire.Fault = "refused-userinfo";
            await Page(async () => await Assert.That(await gateway.IsActiveAsync(refreshed, CancellationToken.None)).IsFalse());
            await Assert.That(wire.Users).IsEqualTo(7);
        }

        [Test]
        public async Task CodeExchangeReadsUserinfoOnceForItsChecks()
        {
            using var wire = new Wire();
            wire.TokenRelease.SetResult();
            wire.PublicRelease.SetResult();
            var gateway = new TsAuthGateway(wire, wire, TimeProvider.System, new TerraScale.Dashboard.Platform.TsAuth.TsAuthClient(wire.CreateClient("platform")));
            var flow = new OidcFlow("state", "nonce", "verifier", "/", "https://dash.terrascale.tech/login/callback", DateTimeOffset.UtcNow.AddMinutes(10));
            await Page(async () =>
            {
                var session = await gateway.ExchangeAsync("code", flow, CancellationToken.None);
                await Assert.That(session.UserId).IsEqualTo("user");
                await Assert.That(wire.Users).IsEqualTo(1);
            });
            wire.Fault = "substituted-userinfo";
            await Page(async () => await Assert.ThrowsAsync<SignInRejectedException>(() => gateway.ExchangeAsync("code", flow, CancellationToken.None)));
            wire.Fault = "refused-userinfo";
            await Page(async () => await Assert.ThrowsAsync<SignInRejectedException>(() => gateway.ExchangeAsync("code", flow, CancellationToken.None)));
        }

        [Test]
        [Arguments("wrong-issuer")]
        [Arguments("untrusted-discovery")]
        [Arguments("wrong-audience")]
        [Arguments("wrong-subject")]
        [Arguments("bad-signature")]
        [Arguments("expired-id")]
        [Arguments("unverified-id")]
        [Arguments("refused-userinfo")]
        [Arguments("unverified-userinfo")]
        [Arguments("substituted-userinfo")]
        public async Task ParallelRefreshStillRejectsInvalidIdentity(string fault)
        {
            using var wire = new Wire { Fault = fault };
            wire.TokenRelease.SetResult();
            wire.PublicRelease.SetResult();
            var gateway = new TsAuthGateway(wire, wire, TimeProvider.System, new TerraScale.Dashboard.Platform.TsAuth.TsAuthClient(wire.CreateClient("platform")));
            await Page(async () => await Assert.ThrowsAsync<SignInRejectedException>(() => gateway.RefreshAsync(Session(), CancellationToken.None)));
        }

        private static Task Page(Func<Task> read)
        {
            return ServerTiming.MiddlewareAsync(
            new DefaultHttpContext { Request = { Method = "GET" } }, read, NullLogger.Instance);
        }

        private static SessionRecord Session()
        {
            return new("user", "Synthetic", "user@example.test", "old", "refresh", DateTimeOffset.UtcNow.AddMinutes(5))
            { RefreshLease = "lease", RefreshLeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1) };
        }

        private sealed class Wire : HttpMessageHandler, IHttpClientFactory, IDashboardIdentity
        {
            private ECDsa signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            public TaskCompletionSource TokenStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource TokenRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource PublicRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource UserinfoStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Metadata, Keys, Users;
            public string Fault { get; set; } = "";
            public void RotateKey() { signing.Dispose(); signing = ECDsa.Create(ECCurve.NamedCurves.nistP256); }
            public HttpClient CreateClient(string name)
            {
                return new(new ServerTiming.Handler { InnerHandler = this }, disposeHandler: false);
            }

            public Task<DashboardCredentials> ReadAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(new DashboardCredentials("synthetic", "", new("", ""), ""));
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                object body;
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("openid-configuration", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref Metadata);
                    await PublicRelease.Task.WaitAsync(cancellationToken);
                    body = new
                    {
                        issuer = Fault == "untrusted-discovery" ? "https://other.example/terrascale" : "https://auth.terrascale.tech/terrascale",
                        id_token_signing_alg_values_supported = new[] { "ES256" }
                    };
                }
                else if (path.EndsWith("jwks.json", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref Keys);
                    await PublicRelease.Task.WaitAsync(cancellationToken);
                    var key = signing.ExportParameters(false);
                    body = new { keys = new[] { new { kty = "EC", crv = "P-256", alg = "ES256", use = "sig", kid = "current", x = B64(key.Q.X!), y = B64(key.Q.Y!) } } };
                }
                else if (path.EndsWith("/token", StringComparison.Ordinal))
                {
                    TokenStarted.TrySetResult();
                    await TokenRelease.Task.WaitAsync(cancellationToken);
                    var unsigned = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = "current" })) + "." + B64(JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        iss = Fault == "wrong-issuer" ? "https://other.example" : "https://auth.terrascale.tech/terrascale",
                        aud = Fault == "wrong-audience" ? "other" : (await ReadAsync(cancellationToken)).AuthClientId,
                        sub = Fault == "wrong-subject" ? "other" : "user",
                        email = "user@example.test",
                        email_verified = Fault != "unverified-id",
                        nonce = "nonce",
                        exp = DateTimeOffset.UtcNow.AddMinutes(Fault == "expired-id" ? -1 : 5).ToUnixTimeSeconds()
                    }));
                    var signature = signing.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                    if (Fault == "bad-signature")
                    {
                        signature[0] ^= 1;
                    }

                    body = new { access_token = "fresh", refresh_token = "rotated", token_type = "Bearer", expires_in = 300, id_token = unsigned + "." + B64(signature) };
                }
                else
                {
                    if (request.Method == HttpMethod.Post)
                    {
                        return new(HttpStatusCode.OK);
                    }

                    Interlocked.Increment(ref Users);
                    UserinfoStarted.TrySetResult();
                    if (Fault == "refused-userinfo")
                    {
                        return new(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":\"invalid_token\"}", Encoding.UTF8, "application/json") };
                    }

                    body = new { sub = Fault == "substituted-userinfo" ? "other" : "user", email = "user@example.test", email_verified = Fault != "unverified-userinfo" };
                }
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
            }
            protected override void Dispose(bool disposing) { if (disposing) { signing.Dispose(); } base.Dispose(disposing); }
            private static string B64(byte[] bytes)
            {
                return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }
        }
    }
}
