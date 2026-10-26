using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerraScale.DotNet.Tests.TerraBase;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAuthMfaPolicyTests
{
    private const string Id = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string Organization = """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAV","name":"Synthetic","created_by":"01ARZ3NDEKTSV4RRFFQ69G5FAV","created_at":"2026-10-26T00:00:00Z","role":null,"require_mfa":false}""";
    private const string Account = """{"enabled":false,"recovery_codes_remaining":0,"manage_url":"https://auth.example.test/ui/terrascale/security"}""";

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task PolicyBooleanIsAlwaysSentAndRequiredOrganizationPolicyRoundTrips(bool require)
    {
        var calls = 0;
        using var http = new HttpClient(new TerraBaseTestHandler(async (request, token) =>
        {
            calls++;
            await Assert.That(await request.Content!.ReadAsStringAsync(token)).IsEqualTo(require ? "{\"require_mfa\":true}" : "{\"require_mfa\":false}");
            return Response(200, Organization.Replace("false", require ? "true" : "false", StringComparison.Ordinal));
        }));
        var organization = await Client(http).Platform.SetOrganizationMfaPolicyAsync("fixture-sign-in", Id, new(require));
        await Assert.That(organization.RequireMfa).IsEqualTo(require);
        await Assert.That(organization.Role).IsNull();
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [Arguments("organization", "require_mfa")]
    [Arguments("account", "enabled")]
    [Arguments("account", "recovery_codes_remaining")]
    [Arguments("account", "manage_url")]
    public async Task RequiredResponseFieldsRejectMissingAndNullWithoutLeakingBody(string target, string field)
    {
        foreach (var missing in new[] { true, false })
        {
            var body = JsonNode.Parse(target == "organization" ? Organization : Account)!;
            if (missing) body.AsObject().Remove(field); else body[field] = null;
            using var http = new HttpClient(new TerraBaseTestHandler((_, _) => Task.FromResult(Response(200, body.ToJsonString()))));
            var error = await Assert.ThrowsAsync<TerraScaleAuthException>(() => target == "organization"
                ? (Task)Client(http).Platform.GetOrganizationAsync("fixture-sign-in", Id) : Client(http).Platform.GetAccountMfaAsync("fixture-sign-in"));
            await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
            await Assert.That(error.ToString()).DoesNotContain("Synthetic");
            await Assert.That(error.InnerException).IsNull();
        }
    }

    [Test]
    [Arguments(403, "mfa_required", "mfa_required")]
    [Arguments(409, "mfa_not_enrolled", "mfa_not_enrolled")]
    [Arguments(403, "mfa_required private-canary", null)]
    [Arguments(400, "invalid_request", "invalid_request")]
    [Arguments(503, "temporarily_unavailable", "temporarily_unavailable")]
    public async Task PolicyRefusalsRemainExactAndNeverReplay(int status, string code, string? expected)
    {
        var calls = 0;
        using var http = new HttpClient(new TerraBaseTestHandler(async (request, token) =>
        {
            calls++;
            await Assert.That(await request.Content!.ReadAsStringAsync(token)).IsEqualTo("{\"require_mfa\":true}");
            var response = Response(status, JsonSerializer.Serialize(new { error = new { code, message = "private-canary" } }));
            response.Headers.RetryAfter = new(TimeSpan.Zero);
            return response;
        }));
        var error = await Assert.ThrowsAsync<TerraScaleAuthException>(() => Client(http).Platform.SetOrganizationMfaPolicyAsync("fixture-sign-in", Id, new(true)));
        await Assert.That(error!.StatusCode).IsEqualTo((HttpStatusCode)status);
        await Assert.That(error.ErrorCode).IsEqualTo(expected);
        await Assert.That(error.ToString()).DoesNotContain("private-canary");
        await Assert.That(error.InnerException).IsNull();
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task MfaCallsValidatePathsAndHonorCancellationBeforeAndDuringDispatch(bool policy)
    {
        var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new TerraBaseTestHandler(async (_, token) =>
        { calls++; entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Response(200, Account); }));
        var platform = Client(http).Platform;
        Task Invoke(CancellationToken token) => policy ? platform.SetOrganizationMfaPolicyAsync("fixture-sign-in", Id, new(true), token) : platform.GetAccountMfaAsync("fixture-sign-in", token);
        await Assert.ThrowsAsync<ArgumentException>(() => platform.SetOrganizationMfaPolicyAsync("fixture-sign-in", "../other", new(true)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => platform.SetOrganizationMfaPolicyAsync("fixture-sign-in", Id, null!));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Invoke(cancellation.Token));
        await Assert.That(calls).IsEqualTo(0);
        using var inFlight = new CancellationTokenSource();
        var operation = Invoke(inFlight.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); inFlight.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => operation);
        await Assert.That(calls).IsEqualTo(1);
    }

    private static HttpResponseMessage Response(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static TerraScaleAuthClient Client(HttpClient http) => new(http, new() { BaseAddress = new Uri("https://auth.example.test"), InstanceSlug = "terrascale" });
}
