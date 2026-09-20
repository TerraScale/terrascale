namespace TerraScale.Auth.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet;

/// <summary>
/// The TerraScale.DotNet ts-auth client (TerraScaleAuthClient) against the real
/// service over the real embedded engine: every management family, the OIDC
/// endpoints, and webhook signature verification end to end. This is the
/// SDK-consumer journey — raw HTTP appears only where the fixture requires it.
/// </summary>
[NotInParallel]
public sealed class AuthSdkE2eTests
{
    private static async Task<(AuthServiceFixture Service, TerraScaleAuthClient Client, string Slug)> StartAsync()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (_, slug, apiKey) = await service.CreateInstanceAsync("sdk-" + Guid.NewGuid().ToString("N")[..8]);
        var client = new TerraScaleAuthClient(service.Http, new TerraScaleAuthClientOptions
        {
            BaseAddress = service.Http.BaseAddress!,
            ApiKey = apiKey,
            InstanceSlug = slug,
        });
        return (service, client, slug);
    }

    [Test]
    public async Task InstanceSettingsRoundTrip()
    {
        var (_, client, slug) = await StartAsync();

        var instance = await client.Instance.GetAsync();
        await Assert.That(instance.Slug).IsEqualTo(slug);
        await Assert.That(instance.Name).IsNotNull();
        await Assert.That(instance.LogoUrl).IsNull();

        var updated = await client.Instance.UpdateAsync(
            name: "Renamed Co",
            logoUrl: "https://cdn.example.test/logo.png",
            themeColor: "#6C5CE7");
        await Assert.That(updated.Name).IsEqualTo("Renamed Co");
        await Assert.That(updated.LogoUrl).IsEqualTo("https://cdn.example.test/logo.png");
        await Assert.That(updated.ThemeColor).IsEqualTo("#6C5CE7");

        var reloaded = await client.Instance.GetAsync();
        await Assert.That(reloaded.Name).IsEqualTo("Renamed Co");
        await Assert.That(reloaded.CreatedAt).IsGreaterThan(0L);

        var invalid = await Assert.ThrowsAsync<TerraScaleApiException>(
            () => client.Instance.UpdateAsync(logoUrl: "javascript:alert(1)"));
        await Assert.That(invalid!.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(invalid.ErrorCode).IsEqualTo("invalid_logo_url");
    }

    [Test]
    public async Task UserLifecycleRoundTrip()
    {
        var (_, client, _) = await StartAsync();
        var email = $"sdk-{Guid.NewGuid():N}@example.test";

        var created = await client.Users.CreateAsync(email: email, username: "sdkuser", password: "password-123", name: "Sdk User");
        await Assert.That(created.Email).IsEqualTo(email);
        await Assert.That(created.EmailVerified).IsFalse();
        await Assert.That(created.Banned).IsFalse();

        var duplicate = await Assert.ThrowsAsync<TerraScaleApiException>(() => client.Users.CreateAsync(email: email));
        await Assert.That(duplicate!.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(duplicate.ErrorCode).IsEqualTo("email_taken");

        var fetched = await client.Users.GetAsync(created.Id);
        await Assert.That(fetched.Name).IsEqualTo("Sdk User");

        var listed = await client.Users.ListAsync(limit: 100);
        await Assert.That(listed.Data.Any(user => user.Id == created.Id)).IsTrue();

        var enumerated = new List<TerraScaleAuthUser>();
        await foreach (var user in client.Users.EnumerateAsync(pageSize: 100))
        {
            enumerated.Add(user);
            if (enumerated.Count > 500)
            {
                throw new InvalidOperationException("user enumeration did not terminate");
            }
        }

        await Assert.That(enumerated.Any(user => user.Id == created.Id)).IsTrue();

        var patched = await client.Users.UpdateAsync(created.Id, patch => patch
            .Name("New Name")
            .PublicMetadata(new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["tier"] = System.Text.Json.JsonDocument.Parse("\"pro\"").RootElement.Clone(),
            })
            .Banned(true)
            .BanReason("abuse"));
        await Assert.That(patched.Name).IsEqualTo("New Name");
        await Assert.That(patched.Banned).IsTrue();
        await Assert.That(patched.BanReason).IsEqualTo("abuse");
        await Assert.That(patched.PublicMetadata!["tier"].GetString()).IsEqualTo("pro");

        var unbanned = await client.Users.UpdateAsync(created.Id, patch => patch.Banned(false));
        await Assert.That(unbanned.Banned).IsFalse();
        await Assert.That(unbanned.BanReason).IsNull();

        var impersonation = await client.Users.ImpersonateAsync(created.Id);
        await Assert.That(impersonation.Token).IsNotNull();
        await Assert.That(impersonation.ExpiresAt).IsGreaterThan(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var sessions = await client.Users.ListSessionsAsync(created.Id);
        await Assert.That(sessions.Data.Any(session => session.Status == "active")).IsTrue();

        await client.Sessions.RevokeAsync(sessions.Data[0].Id);
        var afterRevoke = await client.Users.ListSessionsAsync(created.Id);
        await Assert.That(afterRevoke.Data.Any(session => session.Id == sessions.Data[0].Id && session.Status == "revoked")).IsTrue();

        await client.Users.RevokeAllSessionsAsync(created.Id);
        var drained = await client.Users.ListSessionsAsync(created.Id);
        await Assert.That(drained.Data.Any(session => session.Status == "active")).IsFalse();

        var factors = await client.Users.ListMfaFactorsAsync(created.Id);
        await Assert.That(factors.Data).IsEmpty();
        var passkeys = await client.Users.ListPasskeysAsync(created.Id);
        await Assert.That(passkeys.Passkeys).IsEmpty();
        var organizations = await client.Users.ListOrganizationsAsync(created.Id);
        await Assert.That(organizations).IsEmpty();

        await client.Users.DeleteAsync(created.Id);
        var deleted = await Assert.ThrowsAsync<TerraScaleApiException>(() => client.Users.GetAsync(created.Id));
        await Assert.That(deleted!.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(deleted.ErrorCode).IsEqualTo("user_not_found");
    }

    [Test]
    public async Task ApiKeyLifecycleAndScoping()
    {
        var (service, client, slug) = await StartAsync();

        var created = await client.ApiKeys.CreateAsync("ci-key", ["users"]);
        await Assert.That(created.ApiKey.StartsWith("sk_", StringComparison.Ordinal)).IsTrue();

        var listed = await client.ApiKeys.ListAsync();
        var entry = listed.Data.Single(key => key.Id == created.Id);
        await Assert.That(entry.Name).IsEqualTo("ci-key");
        await Assert.That(entry.Scopes).Contains("users");
        await Assert.That(entry.Revoked).IsFalse();

        // The minted key works on its scoped family and is rejected elsewhere.
        var scoped = new TerraScaleAuthClient(service.Http, new TerraScaleAuthClientOptions
        {
            BaseAddress = service.Http.BaseAddress!,
            ApiKey = created.ApiKey,
            InstanceSlug = slug,
        });
        _ = await scoped.Users.ListAsync();
        var forbidden = await Assert.ThrowsAsync<TerraScaleApiException>(() => scoped.Webhooks.ListAsync());
        await Assert.That(forbidden!.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(forbidden.ErrorCode).IsEqualTo("insufficient_scope");

        await client.ApiKeys.DeleteAsync(created.Id);
        var revoked = await Assert.ThrowsAsync<TerraScaleApiException>(() => scoped.Users.ListAsync());
        await Assert.That(revoked!.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(revoked.ErrorCode).IsEqualTo("unauthorized");
    }

    [Test]
    public async Task OrganizationMembershipInvitationAndDomainFlow()
    {
        var (_, client, _) = await StartAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var org = await client.Organizations.CreateAsync("Acme Corp", $"acme-{suffix}");
        await Assert.That(org.Slug).IsEqualTo($"acme-{suffix}");

        var renamed = await client.Organizations.RenameAsync(org.Id, "Acme Renamed");
        await Assert.That(renamed.Name).IsEqualTo("Acme Renamed");
        await Assert.That((await client.Organizations.ListAsync()).Any(o => o.Id == org.Id)).IsTrue();

        var member = await client.Users.CreateAsync(email: $"member-{suffix}@example.test");
        var membership = await client.Organizations.AddMemberAsync(org.Id, member.Id, "admin");
        await Assert.That(membership.UserId).IsEqualTo(member.Id);

        var memberships = await client.Organizations.ListMembershipsAsync(org.Id);
        await Assert.That(memberships.Single(m => m.Id == membership.Id).User!.Email).IsEqualTo(member.Email);

        var promoted = await client.Organizations.UpdateMemberRoleAsync(org.Id, membership.Id, "owner");
        await Assert.That(promoted.Role).IsEqualTo("owner");

        var invitation = await client.Organizations.CreateInvitationAsync(org.Id, $"invitee-{suffix}@example.test", "member");
        await Assert.That(invitation.Status).IsEqualTo("pending");
        var revokedInvitation = await client.Organizations.RevokeInvitationAsync(org.Id, invitation.Id);
        await Assert.That(revokedInvitation.Status).IsEqualTo("revoked");

        var domain = await client.Organizations.AddDomainAsync(org.Id, $"acme-{suffix}.test");
        var domains = await client.Organizations.ListDomainsAsync(org.Id);
        await Assert.That(domains.Single(d => d.Id == domain.Id).Domain).IsEqualTo($"acme-{suffix}.test");

        var enrollee = await client.Users.CreateAsync(email: $"someone@acme-{suffix}.test");
        var enrollment = await client.Organizations.EnrollDomainUserAsync($"acme-{suffix}.test", enrollee.Id);
        await Assert.That(enrollment.OrganizationId).IsEqualTo(org.Id);
        var userOrganizations = await client.Users.ListOrganizationsAsync(enrollee.Id);
        await Assert.That(userOrganizations.Any(o => o.Organization?.Id == org.Id && o.Role == "member")).IsTrue();

        var mismatch = await Assert.ThrowsAsync<TerraScaleApiException>(
            () => client.Organizations.EnrollDomainUserAsync($"acme-{suffix}.test", member.Id));
        await Assert.That(mismatch!.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(mismatch.ErrorCode).IsEqualTo("email_domain_mismatch");

        await client.Organizations.RemoveMemberAsync(org.Id, membership.Id);
        await client.Organizations.RemoveDomainAsync(org.Id, domain.Id);
        await client.Organizations.DeleteAsync(org.Id);
        var gone = await Assert.ThrowsAsync<TerraScaleApiException>(() => client.Organizations.GetAsync(org.Id));
        await Assert.That(gone!.ErrorCode).IsEqualTo("organization_not_found");
    }

    [Test]
    public async Task JwtTemplateMinting()
    {
        var (_, client, _) = await StartAsync();

        var template = await client.JwtTemplates.CreateAsync("api-token", new Dictionary<string, string>
        {
            ["sub"] = "{{user.id}}",
            ["email"] = "{{user.email}}",
        });
        await Assert.That(template.LifetimeSeconds).IsEqualTo(300);

        var user = await client.Users.CreateAsync(email: $"jwt-{Guid.NewGuid():N}@example.test");
        var minted = await client.JwtTemplates.MintTokenAsync("api-token", user.Id);
        var segments = minted.Jwt.Split('.');
        await Assert.That(segments.Length).IsEqualTo(3);

        var payload = System.Text.Json.JsonDocument.Parse(Base64UrlDecode(segments[1]));
        await Assert.That(payload.RootElement.GetProperty("email").GetString()).IsEqualTo(user.Email);

        var renamed = await client.JwtTemplates.UpdateAsync("api-token", newName: "api-token-v2");
        await Assert.That(renamed.Name).IsEqualTo("api-token-v2");
        await Assert.That((await client.JwtTemplates.ListAsync()).Any(t => t.Name == "api-token-v2")).IsTrue();

        await client.JwtTemplates.DeleteAsync("api-token-v2");
        var deleted = await Assert.ThrowsAsync<TerraScaleApiException>(() => client.JwtTemplates.MintTokenAsync("api-token-v2", user.Id));
        await Assert.That(deleted!.ErrorCode).IsEqualTo("template_not_found");
    }

    [Test]
    public async Task OAuthClientsDiscoveryJwksAndMachineTokens()
    {
        var (_, client, slug) = await StartAsync();

        var created = await client.OAuthClients.CreateAsync(
            "backend",
            ["https://app.example.test/callback"],
            isPublic: false,
            tokenEndpointAuthMethod: "client_secret_post");
        await Assert.That(created.ClientSecret).IsNotNull();
        await Assert.That(created.Client.ClientId).IsNotNull();
        var clientSecret = created.ClientSecret!;

        var clients = await client.OAuthClients.ListAsync();
        await Assert.That(clients.Clients.Any(c => c.Id == created.Client.Id)).IsTrue();

        var discovery = await client.Oidc.GetDiscoveryDocumentAsync();
        await Assert.That(discovery.Issuer.EndsWith("/" + slug, StringComparison.Ordinal)).IsTrue();
        await Assert.That(discovery.GrantTypesSupported.Contains("client_credentials")).IsTrue();
        await Assert.That(discovery.IdTokenSigningAlgValuesSupported.Contains("ES256")).IsTrue();

        var jwks = await client.Oidc.GetJwksAsync();
        await Assert.That(jwks.Keys).IsNotEmpty();
        await Assert.That(jwks.Keys[0].KeyType).IsEqualTo("EC");

        var token = await client.Oidc.RequestClientCredentialsTokenAsync(created.Client.ClientId, created.ClientSecret!);
        await Assert.That(token.AccessToken).IsNotNull();
        await Assert.That(token.RefreshToken).IsNull();
        await Assert.That(token.TokenType).IsEqualTo("Bearer");

        var introspected = await client.Oidc.IntrospectTokenAsync(created.Client.ClientId, clientSecret, token.AccessToken);
        await Assert.That(introspected.Active).IsTrue();
        await Assert.That(introspected.Subject).IsEqualTo(created.Client.ClientId);

        await client.Oidc.RevokeTokenAsync(created.Client.ClientId, clientSecret, token.AccessToken);

        await client.OAuthClients.DeleteAsync(created.Client.Id);
        var afterDelete = await Assert.ThrowsAsync<TerraScaleApiException>(
            () => client.Oidc.RequestClientCredentialsTokenAsync(created.Client.ClientId, clientSecret));
        await Assert.That(afterDelete!.ErrorCode).IsEqualTo("invalid_client");
    }

    [Test]
    public async Task ConnectionsAndDirectories()
    {
        var (_, client, _) = await StartAsync();

        var (certificate, pem) = NewIdpCertificate();
        using (certificate)
        {
            var saml = await client.Connections.CreateSamlAsync(
                "Corp IdP",
                "https://idp.example.test/metadata",
                "https://idp.example.test/sso",
                pem);
            await Assert.That(saml.Type).IsEqualTo("saml");
            await Assert.That(saml.Enabled).IsTrue();

            var oidc = await client.Connections.CreateOidcAsync(
                "Google",
                "https://accounts.example.test",
                "google-client",
                "google-secret",
                authorizationEndpoint: "https://accounts.example.test/o/oauth2/v2/auth",
                tokenEndpoint: "https://oauth2.example.test/token");
            await Assert.That(oidc.Type).IsEqualTo("oidc");
            await Assert.That(oidc.IssuerUrl).IsEqualTo("https://accounts.example.test");

            var connections = await client.Connections.ListAsync();
            await Assert.That(connections.Data.Count(c => c.Id == saml.Id || c.Id == oidc.Id)).IsEqualTo(2);

            var disabled = await client.Connections.UpdateAsync(saml.Id, enabled: false);
            await Assert.That(disabled.Enabled).IsFalse();

            await client.Connections.DeleteAsync(oidc.Id);
            await client.Connections.DeleteAsync(saml.Id);
        }

        var directory = await client.Directories.CreateAsync();
        await Assert.That(directory.Token.StartsWith("scim_", StringComparison.Ordinal)).IsTrue();

        var directories = await client.Directories.ListAsync();
        await Assert.That(directories.Data.Any(d => d.Id == directory.Id)).IsTrue();

        var rotated = await client.Directories.RotateTokenAsync(directory.Id);
        await Assert.That(rotated.Token.StartsWith("scim_", StringComparison.Ordinal)).IsTrue();
        await Assert.That(rotated.Token).IsNotEqualTo(directory.Token);

        await client.Directories.DeleteAsync(directory.Id);
    }

    [Test]
    public async Task WebhooksDeliverSignedEventsTheSduCanVerify()
    {
        var (_, client, _) = await StartAsync();
        await using var sink = new WebhookSink();
        {
            // The dead endpoint answers 500, so its deliveries exhaust the three
            // attempts and land in the dead-letter store with a last_error.
            var dead = await client.Webhooks.CreateAsync(sink.DeadUrl, ["*"]);
            var deadTest = await client.Webhooks.SendTestEventAsync(dead.Id);
            await Assert.That(deadTest.Queued).IsTrue();

            await PollAsync(() => Task.FromResult(sink.DeadAttempts >= 3 ? true : (bool?)null), TimeSpan.FromSeconds(30));
            var deadEntry = await PollAsync(async () =>
            {
                var entries = await client.Webhooks.ListDeadLetterAsync();
                return entries.FirstOrDefault(entry => entry.EndpointId == dead.Id && entry.Attempts >= 3);
            }, TimeSpan.FromSeconds(30));
            await Assert.That(deadEntry).IsNotNull();
            await Assert.That(deadEntry!.LastError).IsEqualTo("HTTP 500");
            await Assert.That(deadEntry.Attempts).IsEqualTo(3);

            // Replay re-delivers synchronously; the sink still 500s, so the
            // delivery dies again and the endpoint reports 404 not-dead.
            var replayDead = await Assert.ThrowsAsync<TerraScaleApiException>(
                () => client.Webhooks.ReplayDeadDeliveryAsync(deadEntry.Id));
            await Assert.That(replayDead!.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

            var healthy = await client.Webhooks.CreateAsync(sink.Url, ["*"]);
            await Assert.That(healthy.SigningSecret.Length).IsGreaterThanOrEqualTo(32);

            var listed = await client.Webhooks.ListAsync();
            var stored = listed.Single(w => w.Id == healthy.Id);
            await Assert.That(stored.Enabled).IsTrue();

            var test = await client.Webhooks.SendTestEventAsync(healthy.Id);
            await Assert.That(test.Queued).IsTrue();

            var delivery = await sink.WaitForDeliveryAsync(TimeSpan.FromSeconds(15));
            var body = Encoding.UTF8.GetString(delivery.Body);
            using (var document = JsonDocument.Parse(body))
            {
                await Assert.That(document.RootElement.GetProperty("type").GetString()).IsEqualTo("webhook.test");
            }

            await Assert.That(TerraScaleAuthWebhookVerifier.Verify(
                healthy.SigningSecret,
                delivery.Timestamp,
                delivery.Signature,
                delivery.Body)).IsTrue();
            await Assert.That(TerraScaleAuthWebhookVerifier.Verify(
                healthy.SigningSecret,
                delivery.Timestamp,
                delivery.Signature,
                Encoding.UTF8.GetBytes(body + " "))).IsFalse();

            var deadLetter = await client.Webhooks.ListDeadLetterAsync();
            // The healthy endpoint accepted its event: nothing of its own may be dead.
            await Assert.That(deadLetter.All(d => d.EndpointId != healthy.Id)).IsTrue();

            var replayUnknown = await Assert.ThrowsAsync<TerraScaleApiException>(
                () => client.Webhooks.ReplayDeadDeliveryAsync($"{healthy.Id}:nonexistent"));
            await Assert.That(replayUnknown!.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

            var audit = await client.AuditEvents.ListAsync(limit: 100);
            await Assert.That(audit.Any(e => e.Type == "webhook.created")).IsTrue();
            await Assert.That(audit.All(e => e.IntegrityHash.Length > 0)).IsTrue();

            await client.Webhooks.DeleteAsync(healthy.Id);
            await client.Webhooks.DeleteAsync(dead.Id);
        }
    }

    private static async Task<T?> PollAsync<T>(Func<Task<T?>> probe, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await probe() is { } found)
            {
                return found;
            }

            await Task.Delay(500);
        }

        return await probe();
    }

    [Test]
    public async Task BillingSurfaceAndQuotaRepair()
    {
        var (_, client, _) = await StartAsync();

        var overview = await client.Billing.GetOverviewAsync();
        await Assert.That(overview.Plan).IsEqualTo("free");
        await Assert.That(overview.Quota).IsEqualTo(100);

        var decision = await client.Billing.EnforceQuotaAsync(0);
        await Assert.That(decision.Allowed).IsTrue();

        // The fixture has no Stripe configuration; checkout reports it precisely.
        var unconfigured = await Assert.ThrowsAsync<TerraScaleApiException>(
            () => client.Billing.CreateCheckoutAsync("pro"));
        await Assert.That(unconfigured!.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(unconfigured.ErrorCode).IsEqualTo("billing_not_configured");

        var invalidPlan = await Assert.ThrowsAsync<TerraScaleApiException>(
            () => client.Billing.CreateCheckoutAsync("free"));
        await Assert.That(invalidPlan!.ErrorCode).IsEqualTo("invalid_plan");

        var reconciled = await client.Billing.ReconcileQuotaAsync();
        await Assert.That(reconciled.ClaimsExamined).IsEqualTo(0);
    }

    private static string Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/').PadRight(value.Length + (4 - value.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private static (X509Certificate2 Certificate, string Pem) NewIdpCertificate()
    {
        var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Test SAML IdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        return (certificate, certificate.ExportCertificatePem());
    }

    /// <summary>Local receiver for dispatcher deliveries; the SDK's signature
    /// verifier is exercised against the exact bytes that arrived.</summary>
    private sealed class WebhookSink : IAsyncDisposable
    {
        private readonly HttpListener listener = new();
        private readonly ConcurrentQueue<(string Timestamp, string Signature, byte[] Body)> deliveries = new();
        private int deadAttempts;

        public string Url { get; }

        /// <summary>The path this sink answers 500 on, for dead-letter flows.</summary>
        public string DeadUrl => Url + "dead";

        /// <summary>How many delivery attempts hit <see cref="DeadUrl"/>.</summary>
        public int DeadAttempts => Volatile.Read(ref deadAttempts);

        public WebhookSink()
        {
            Url = FindSocket();
            listener.Prefixes.Add(Url);
            listener.Start();
            _ = ListenAsync();
        }

        public async Task<(string Timestamp, string Signature, byte[] Body)> WaitForDeliveryAsync(TimeSpan timeout) =>
            await WaitForDeliveryAsync(delivery =>
            {
                using var document = System.Text.Json.JsonDocument.Parse(Encoding.UTF8.GetString(delivery.Body));
                return document.RootElement.GetProperty("type").GetString() == "webhook.test";
            }, timeout);

        /// <summary>A "*" endpoint also receives its own webhook.created event
        /// before the test event; wait for the specific delivery under test.</summary>
        public async Task<(string Timestamp, string Signature, byte[] Body)> WaitForDeliveryAsync(
            Func<(string Timestamp, string Signature, byte[] Body), bool> predicate,
            TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (deliveries.TryDequeue(out var delivery) && predicate(delivery))
                {
                    return delivery;
                }

                await Task.Delay(200);
            }

            throw new TimeoutException($"no matching webhook delivery within {timeout.TotalSeconds:0}s");
        }

        private async Task ListenAsync()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                using var reader = new BinaryReader(context.Request.InputStream);
                var body = reader.ReadBytes((int)context.Request.ContentLength64);
                if (context.Request.Url?.AbsolutePath == "/dead")
                {
                    Volatile.Write(ref deadAttempts, Volatile.Read(ref deadAttempts) + 1);
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                    continue;
                }

                deliveries.Enqueue((
                    context.Request.Headers["X-TsAuth-Timestamp"] ?? string.Empty,
                    context.Request.Headers["X-TsAuth-Signature"] ?? string.Empty,
                    body));
                context.Response.StatusCode = 200;
                context.Response.Close();
            }
        }

        private static string FindSocket()
        {
            for (var port = 18130; port < 18140; port++)
            {
                try
                {
                    var probe = new HttpListener();
                    probe.Prefixes.Add($"http://127.0.0.1:{port}/");
                    probe.Start();
                    probe.Stop();
                    return $"http://127.0.0.1:{port}/";
                }
                catch (HttpListenerException)
                {
                }
            }

            throw new InvalidOperationException("no free webhook sink port in 18130-18139");
        }

        public async ValueTask DisposeAsync()
        {
            listener.Stop();
            await Task.CompletedTask;
        }
    }
}
