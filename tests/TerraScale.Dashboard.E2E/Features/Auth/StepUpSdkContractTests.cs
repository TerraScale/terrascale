using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.E2E.Features.Auth;

public sealed class StepUpSdkContractTests
{
    private const string Proof = "synthetic-backend-proof";
    private static readonly string Receipt = new('A', 43);
    private const string Grant = "synthetic-deletion-authorization";
    private static readonly SessionRecord Session = new("synthetic-user", "Synthetic", "synthetic@example.test", "synthetic-user-bearer", null, DateTimeOffset.UtcNow.AddHours(1));

    [Test]
    public async Task InstalledSdkReturnsTheConsumedGrantAndForwardsItWithTheExactReceipt()
    {
        using var wire = new Wire();
        var gateway = new TsAuthGateway(new TestIdentity(), wire, TimeProvider.System);
        var challenge = await gateway.BeginAsync(Session, "delete_account", "ts-dashboard", "https://dash.terrascale.tech/auth/step-up/callback", default);
        await Assert.That(challenge.Proof).IsEqualTo(Proof);
        var grant = await gateway.ConsumeAsync(Session, challenge.Proof, "delete_account", "ts-dashboard", Receipt, default);
        await Assert.That(grant).IsEqualTo(Grant);
        await Assert.That(await gateway.DeleteAccountAsync(Session, Receipt, grant!, default)).IsTrue();
        await Assert.That(await gateway.ConsumeAsync(Session, Proof, "create_pat", "ts-dashboard", null, default)).IsNull();
        await Assert.That(wire.Requests).IsEqualTo(4);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RefusedOrLostConsumptionDoesNotRetryOrReturnAGrant(bool lostAnswer)
    {
        using var wire = new Wire { LoseConsumeAnswer = lostAnswer, Refuse = !lostAnswer };
        var gateway = new TsAuthGateway(new TestIdentity(), wire, TimeProvider.System);
        Exception? rejected = null;
        try { _ = await gateway.ConsumeAsync(Session, Proof, "delete_account", "ts-dashboard", Receipt, default); }
        catch (Exception error) when (error is IOException or AccountAccessException) { rejected = error; }
        await Assert.That(rejected is not null).IsTrue();
        await Assert.That(wire.Requests).IsEqualTo(1);
        if (!lostAnswer) await Assert.That(((AccountAccessException)rejected!).Code).IsEqualTo("step_up_required");
    }

    private sealed class Wire : HttpMessageHandler, IHttpClientFactory
    {
        public int Requests { get; private set; }
        public bool LoseConsumeAnswer { get; init; }
        public bool Refuse { get; init; }
        public HttpClient CreateClient(string name) => new(this, false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            await Assert.That(request.Headers.Authorization?.Parameter).IsEqualTo(Session.AccessToken);
            await Assert.That(request.RequestUri!.Host).IsEqualTo("auth.terrascale.tech");
            if (request.Method == HttpMethod.Delete)
            {
                await Assert.That(request.Headers.GetValues("X-TerraScale-Deletion-Receipt").Single()).IsEqualTo(Receipt);
                await Assert.That(request.Headers.GetValues("X-TerraScale-Deletion-Authorization").Single()).IsEqualTo(Grant);
                await Assert.That(request.Headers.Contains("X-TerraScale-Step-Up-Proof")).IsFalse();
                return new(HttpStatusCode.NoContent);
            }
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var fields = body.RootElement;
            if (request.RequestUri.AbsolutePath.EndsWith("/consume", StringComparison.Ordinal))
            {
                await Assert.That(fields.GetProperty("proof").GetString()).IsEqualTo(Proof);
                if (LoseConsumeAnswer) throw new IOException("Synthetic lost consume response.");
                if (Refuse) return Json(HttpStatusCode.Forbidden, new { error = new { code = "step_up_required", message = "Synthetic refusal" } });
                if (fields.GetProperty("purpose").GetString() == "create_pat") return new(HttpStatusCode.NoContent);
                await Assert.That(fields.GetProperty("operation_receipt").GetString()).IsEqualTo(Receipt);
                return Json(HttpStatusCode.OK, new { operation_grant = Grant });
            }
            await Assert.That(fields.GetProperty("return_uri").GetString()).IsEqualTo("https://dash.terrascale.tech/auth/step-up/callback");
            return Json(HttpStatusCode.Created, new { proof = Proof, step_up_url = "https://auth.terrascale.tech/ui/terrascale/step-up/synthetic", expires_at = DateTimeOffset.UtcNow.AddMinutes(5) });
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
