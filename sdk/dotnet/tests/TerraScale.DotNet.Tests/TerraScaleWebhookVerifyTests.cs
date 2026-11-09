using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleWebhookVerifyTests
{
    private const string Secret = "whsec_AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private const string Body = "{\"type\":\"user.signed_up\",\"data\":{\"user_id\":\"01JDUSER\"}}";
    private const string Timestamp = "1765363200";
    private const string Signature = "myaKa5rQlFeG2BKid6AzyyYZL5dGtB0ogW7TE+30ARg=";

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Test]
    public async Task VerifyWebhookAcceptsSignedDeliveryAndParsesEvent()
    {
        var headers = new Dictionary<string, string>
        {
            ["webhook-id"] = "msg_01JDWEBHOOK0001",
            ["webhook-timestamp"] = Timestamp,
            ["webhook-signature"] = "v1," + Signature,
        };
        var evt = TerraScaleWebhooks.VerifyWebhook(Secret, headers, Body, new Clock(DateTimeOffset.FromUnixTimeSeconds(1765363200)));
        await Assert.That(evt.GetProperty("type").GetString()).IsEqualTo("user.signed_up");
        await Assert.That(evt.GetProperty("data").GetProperty("user_id").GetString()).IsEqualTo("01JDUSER");
    }

    [Test]
    public async Task VerifyWebhookRejectsTamperedBody()
    {
        var headers = new Dictionary<string, string>
        {
            ["webhook-id"] = "msg_01JDWEBHOOK0001",
            ["webhook-timestamp"] = Timestamp,
            ["webhook-signature"] = "v1," + Signature,
        };
        var tampered = "{\"type\":\"user.signed_up\",\"data\":{\"user_id\":\"01JDATTACKER\"}}";
        await Assert.That(() => TerraScaleWebhooks.VerifyWebhook(Secret, headers, tampered, new Clock(DateTimeOffset.FromUnixTimeSeconds(1765363200))))
            .Throws<TerraScaleWebhookException>()
            .WithMessage("No v1 signature entry matched the delivery.");
    }
}
