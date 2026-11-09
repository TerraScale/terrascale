using System.Net;
using System.Net.Http.Headers;
using TerraScale.Authentication.Tests;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Fast.Management;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class StepUpOrganizationTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Organization_deletion_requires_a_fresh_proof_and_succeeds_with_one()
    {
        await using var oidc = await OidcHarness.StartSharedManagementAsync(fixture);
        var (_, token) = await oidc.CreateUserTokenAsync(oidc.UniqueEmail("ora"));
        var organization = await oidc.CreateOrganizationAsync(token);

        using var refused = await oidc.SendAsync(HttpMethod.Delete, $"{Organizations}/{organization}", token);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await OidcHarness.EnvelopeCodeAsync(refused)).IsEqualTo("step_up_required");

        using var deleted = await oidc.CallAsync(HttpMethod.Delete, $"{Organizations}/{organization}", token);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task Ownership_transfer_start_requires_a_fresh_proof_and_succeeds_with_one()
    {
        await using var oidc = await OidcHarness.StartSharedManagementAsync(fixture);
        var (_, token) = await oidc.CreateUserTokenAsync(oidc.UniqueEmail("ola"));
        var (nominee, _) = await oidc.CreateUserTokenAsync(oidc.UniqueEmail("oli"));
        var organization = await oidc.CreateOrganizationAsync(token);
        await oidc.AddMemberAsync(organization, nominee.Id, "member");

        using var refused = await oidc.SendAsync(HttpMethod.Post, $"{Organizations}/{organization}/ownership-transfer", token, new { user_id = nominee.Id });
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await OidcHarness.EnvelopeCodeAsync(refused)).IsEqualTo("step_up_required");

        var transfer = await oidc.OkAsync(HttpMethod.Post, $"{Organizations}/{organization}/ownership-transfer", token, new { user_id = nominee.Id }, HttpStatusCode.Created);
        await Assert.That(transfer.GetProperty("to_user_id").GetString()).IsEqualTo(nominee.Id);
    }
}

file static class StepUpOrganizationCalls
{
    public static async Task<HttpResponseMessage> SendAsync(this OidcHarness oidc, HttpMethod method, string path, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await oidc.Client.SendAsync(request);
    }
}
