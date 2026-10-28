using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Stripe;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E.Features.Billing;

[ClassDataSource<DashboardE2EFixture>]
public sealed class BillingWebhookJourneyTests(DashboardE2EFixture fixture)
{
    private static readonly string[] WebhookPaths =
    [
        "/api/v1/billing/stripe/webhook",
        "/api/auth/stripe/webhook"
    ];

    [Test]
    public async Task CanonicalAndLegacyWebhooksVerifySignaturesAndReconcileCurrentStripeStateAnonymously()
    {
        using var browser = fixture.CreateClient();
        using var anonymous = fixture.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        await BillingPlanJourneyTests.SignUpPaidAsync(browser,
            "billing-webhook-" + suffix + "@example.test", "E2E-webhook-" + suffix, suffix);

        var csrf = await HttpJourney.CsrfAsync(browser, "/organization?tab=billing");
        using var created = await HttpJourney.PostFormAsync(browser, "/__test/billing/webhook/event",
            new() { ["_csrf"] = csrf, ["liveMode"] = "false" });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var eventDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var body = eventDocument.RootElement.GetProperty("body").GetString()!;
        var signature = eventDocument.RootElement.GetProperty("signature").GetString()!;

        foreach (var path in WebhookPaths)
        {
            using var unsigned = await SendWebhookAsync(anonymous, path, body, null);
            await Assert.That(unsigned.StatusCode).IsEqualTo(HttpStatusCode.BadRequest)
                .Because("Neither webhook alias accepts an unsigned event.");
            using var forged = await SendWebhookAsync(anonymous, path, body, "t=1,v1=forged");
            await Assert.That(forged.StatusCode).IsEqualTo(HttpStatusCode.BadRequest)
                .Because("Neither webhook alias accepts a forged signature.");
            const string invalidJson = "{";
            using var malformed = await SendWebhookAsync(anonymous, path, invalidJson,
                EventUtility.GenerateSignatureHeader(invalidJson, "whsec_demo"));
            await Assert.That(malformed.StatusCode).IsEqualTo(HttpStatusCode.BadRequest)
                .Because("A correctly signed malformed body is still rejected.");

            // This old event snapshot says canceled/free. The handler must use
            // its customer only to re-read the current canonical subscription.
            using var accepted = await SendWebhookAsync(anonymous, path, body, signature);
            await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.OK)
                .Because("A valid test-mode event reconciles without browser auth or CSRF.");
            var largerPrefix = body[..^1] + ",\"padding\":\"";
            var largerBody = largerPrefix + new string('x', 1024 * 1024 - largerPrefix.Length - 2) + "\"}";
            await Assert.That(Encoding.UTF8.GetByteCount(largerBody)).IsEqualTo(1024 * 1024);
            using var larger = await SendWebhookAsync(anonymous, path, largerBody,
                EventUtility.GenerateSignatureHeader(largerBody, "whsec_demo"));
            await Assert.That(larger.StatusCode).IsEqualTo(HttpStatusCode.OK)
                .Because("Both aliases retain the original 1 MiB limit rather than inheriting the smaller default form limit.");
            using var chunkedBoundary = await SendWebhookAsync(anonymous, path, largerBody,
                EventUtility.GenerateSignatureHeader(largerBody, "whsec_demo"), chunked: true);
            await Assert.That(chunkedBoundary.StatusCode).IsEqualTo(HttpStatusCode.OK)
                .Because("The exact 1 MiB accepted boundary also works without Content-Length.");
        }

        using var wrongMode = await HttpJourney.PostFormAsync(browser, "/__test/billing/webhook/event",
            new() { ["_csrf"] = csrf, ["liveMode"] = "true" });
        await Assert.That(wrongMode.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var wrongModeDocument = JsonDocument.Parse(await wrongMode.Content.ReadAsStringAsync());
        var wrongModeBody = wrongModeDocument.RootElement.GetProperty("body").GetString()!;
        var wrongModeSignature = wrongModeDocument.RootElement.GetProperty("signature").GetString()!;
        using var rejectedMode = await SendWebhookAsync(anonymous, WebhookPaths[1], wrongModeBody, wrongModeSignature);
        await Assert.That(rejectedMode.StatusCode).IsEqualTo(HttpStatusCode.BadRequest)
            .Because("A valid signature cannot authorize an event from the wrong key mode.");

        using var armedOffline = await HttpJourney.PostFormAsync(browser, "/__test/billing/plans/scenario",
            new() { ["_csrf"] = csrf, ["scenario"] = "offline" });
        await Assert.That(armedOffline.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        try
        {
            using var unavailable = await SendWebhookAsync(anonymous, WebhookPaths[0], body, signature);
            await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable)
                .Because("A valid event actually reads Stripe, and an outage remains retryable rather than acknowledging stale facts.");
            using var offlineWrongMode = await SendWebhookAsync(anonymous, WebhookPaths[1], wrongModeBody, wrongModeSignature);
            await Assert.That(offlineWrongMode.StatusCode).IsEqualTo(HttpStatusCode.BadRequest)
                .Because("Wrong-mode events are rejected before attempting provider reconciliation.");
            const int bodyLimit = 1024 * 1024;
            var oversizedPrefix = body[..^1] + ",\"padding\":\"";
            var oversizedBody = oversizedPrefix + new string('x', bodyLimit + 1 - oversizedPrefix.Length - 2) + "\"}";
            await Assert.That(Encoding.UTF8.GetByteCount(oversizedBody)).IsEqualTo(bodyLimit + 1);
            var oversizedSignature = EventUtility.GenerateSignatureHeader(oversizedBody, "whsec_demo");
            using var oversized = await SendWebhookAsync(anonymous, WebhookPaths[0], oversizedBody, oversizedSignature);
            await Assert.That(oversized.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge)
                .Because("The 1 MiB + 1 body limit rejects before validating or syncing a provider event.");
            using var chunkedOversized = await SendWebhookAsync(anonymous, WebhookPaths[1], oversizedBody, oversizedSignature, chunked: true);
            await Assert.That(chunkedOversized.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge)
                .Because("The same limit applies when chunked HTTP provides no Content-Length.");
        }
        finally
        {
            using var resetProvider = await HttpJourney.PostFormAsync(browser, "/__test/billing/plans/scenario",
                new() { ["_csrf"] = csrf, ["scenario"] = "confirmed" });
            await Assert.That(resetProvider.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }

        using var context = JsonDocument.Parse(await browser.GetStringAsync("/api/v1/dashboard/context"));
        using var me = JsonDocument.Parse(await browser.GetStringAsync("/api/v1/auth/me"));
        var organizationId = me.RootElement.GetProperty("organizationId").GetString();
        var organization = context.RootElement.GetProperty("organizations").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == organizationId);
        await Assert.That(organization.GetProperty("planId").GetString()).IsEqualTo("443RCMMF70MNP6S1QMT230NF99")
            .Because("Duplicate and out-of-order stale snapshots cannot downgrade current provider state.");
        await Assert.That(organization.GetProperty("billingStatus").GetString()).IsEqualTo("active")
            .Because("A validated paid subscription keeps the organization's existing active billing entitlement.");
    }

    [Test]
    [Arguments(false, 1)]
    [Arguments(true, 1)]
    [Arguments(false, 64 * 1024 + 1)]
    [Arguments(true, 64 * 1024 + 1)]
    public async Task OversizedWebhookAllowsFollowUpPostOnSameClientPool(bool chunked, int excess)
    {
        using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, MaxConnectionsPerServer = 1 })
        { BaseAddress = fixture.Origin, Timeout = TimeSpan.FromSeconds(30) };
        var body = new string('x', (1 << 20) + excess);
        foreach (var path in WebhookPaths)
        {
            using (var rejected = await SendWebhookAsync(client, path, body, "t=1,v1=forged", chunked))
            {
                await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
                if (excess > 64 * 1024)
                    await Assert.That(rejected.Headers.ConnectionClose).IsTrue()
                        .Because("An undrained HTTP/1.1 rejection must tell the pool to retire its connection.");
            }

            using var followUp = await SendWebhookAsync(client, path, "{}", "t=1,v1=forged");
            await Assert.That(followUp.StatusCode).IsEqualTo(HttpStatusCode.BadRequest)
                .Because("A subsequent non-idempotent POST must receive a real response without a transport retry.");
        }
    }

    [Test]
    public async Task KnownLengthOversizedWebhookIsDrainedAfterEarlyRejection()
    {
        // Raw HTTP pins the order HttpClient races: 413 arrives, then the sender finishes its upload.
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(fixture.Origin.Host, fixture.Origin.Port);
        using var stream = new SslStream(tcp.GetStream(), false, (_, certificate, _, errors) => TestHttps.Valid(certificate, errors));
        await stream.AuthenticateAsClientAsync(fixture.Origin.Host);
        const int length = (1 << 20) + 1;
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"POST {WebhookPaths[0]} HTTP/1.1\r\nHost: {fixture.Origin.Authority}\r\nContent-Type: application/json\r\nStripe-Signature: t=1,v1=forged\r\nContent-Length: {length}\r\n\r\n"));
        await Assert.That(await ReadResponseHeadAsync(stream)).StartsWith("HTTP/1.1 413")
            .Because("A known oversized length is rejected before any body byte is read.");
        await stream.WriteAsync(new byte[length]);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET /readyz HTTP/1.1\r\nHost: {fixture.Origin.Authority}\r\n\r\n"));
        await Assert.That(await ReadResponseHeadAsync(stream)).StartsWith("HTTP/1.1 200")
            .Because("The rejected upload is drained within the bounded allowance instead of resetting the sender.");
    }

    private static async Task<string> ReadResponseHeadAsync(Stream stream)
    {
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one) == 0) throw new IOException("Connection closed after " + head.Length + " response bytes.");
            head.Append((char)one[0]);
        }
        var contentLength = Regex.Match(head.ToString(), "Content-Length: (\\d+)", RegexOptions.IgnoreCase);
        if (contentLength.Success) await stream.ReadExactlyAsync(new byte[int.Parse(contentLength.Groups[1].Value)]);
        else if (head.ToString().Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase))
        {
            // Error responses now carry problem JSON; consume its framing before the next response.
            while (true)
            {
                var line = new StringBuilder();
                while (!line.ToString().EndsWith("\r\n", StringComparison.Ordinal))
                {
                    await stream.ReadExactlyAsync(one);
                    line.Append((char)one[0]);
                }
                var size = int.Parse(line.ToString().Trim().Split(';')[0], System.Globalization.NumberStyles.HexNumber);
                await stream.ReadExactlyAsync(new byte[size + 2]);
                if (size == 0) break;
            }
        }
        return head.ToString();
    }

    private static async Task<HttpResponseMessage> SendWebhookAsync(HttpClient client, string path, string body, string? signature, bool chunked = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (signature is not null) request.Headers.TryAddWithoutValidation("Stripe-Signature", signature);
        if (chunked) request.Headers.TransferEncodingChunked = true;
        return await client.SendAsync(request);
    }
}
