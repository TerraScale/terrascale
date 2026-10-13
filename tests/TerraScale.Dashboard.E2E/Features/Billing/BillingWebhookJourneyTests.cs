using System.Net;
using System.Text;
using System.Text.Json;
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
            // Separate framing checks from connection-reuse behavior tracked in https://github.com/TerraScale/ts-dashboard/issues/18.
            using var knownLengthClient = fixture.CreateClient();
            using var oversized = await SendWebhookAsync(knownLengthClient, WebhookPaths[0], oversizedBody, oversizedSignature);
            await Assert.That(oversized.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge)
                .Because("The 1 MiB + 1 body limit rejects before validating or syncing a provider event.");
            using var chunkedClient = fixture.CreateClient();
            using var chunkedOversized = await SendWebhookAsync(chunkedClient, WebhookPaths[1], oversizedBody, oversizedSignature, chunked: true);
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
            .Because("A validated paid-plan trial keeps the organization's existing active billing entitlement.");
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
