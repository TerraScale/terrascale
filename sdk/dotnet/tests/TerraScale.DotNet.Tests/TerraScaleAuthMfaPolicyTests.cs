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

    private static HttpResponseMessage Response(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static TerraScaleAuthClient Client(HttpClient http) => new(http, new() { BaseAddress = new Uri("https://auth.example.test"), InstanceSlug = "terrascale" });
}
