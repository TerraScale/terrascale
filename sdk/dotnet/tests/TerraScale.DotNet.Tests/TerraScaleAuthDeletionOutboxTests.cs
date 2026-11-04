using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAuthDeletionOutboxTests
{
    private const string Id = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string Subject = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
    private const string Organization = "01ARZ3NDEKTSV4RRFFQ69G5FAX";

    [Test]
    [Arguments("account", null)]
    [Arguments("organization", Subject)]
    [Arguments("project", Organization)]
    public async Task ListAndAcknowledgeUseBasicInstancePathAndExactProof(string scope, string? organization)
    {
        var proof = new TerraScaleAuthDeletionProof(Id, scope, Subject, organization);
        using var handler = new Handler(async (request, ct) =>
        {
            await Assert.That(request.Headers.Authorization?.ToString()).IsEqualTo("Basic " +
                Convert.ToBase64String(Encoding.ASCII.GetBytes("fixture%3Aclient:fixture%20secret")));
            await Assert.That(request.Headers.Accept.Single().MediaType).IsEqualTo("application/json");
            await Assert.That(request.Headers.Contains("X-TerraScale-Deletion-Receipt")).IsFalse();
            if (request.Method == HttpMethod.Get)
            {
                await Assert.That(request.RequestUri!.PathAndQuery).IsEqualTo("/v1/private-instance/deletions");
                await Assert.That(request.Content).IsNull();
                return Json(Page(proof));
            }
            await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
            await Assert.That(request.RequestUri!.PathAndQuery).IsEqualTo($"/v1/private-instance/deletions/{Id}/acknowledge");
            await Assert.That(request.Content!.Headers.ContentType!.MediaType).IsEqualTo("application/json");
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            await Assert.That(body.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray())
                .IsEquivalentTo(new[] { "organizationId", "scope", "subjectId" });
            await Assert.That(body.RootElement.GetProperty("scope").GetString()).IsEqualTo(scope);
            await Assert.That(body.RootElement.GetProperty("subjectId").GetString()).IsEqualTo(Subject);
            await Assert.That(body.RootElement.GetProperty("organizationId").GetString()).IsEqualTo(organization);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fixture-default");
        var platform = Client(http).Platform;
        var page = await platform.ListDeletionsAsync("fixture:client", "fixture secret");
        await Assert.That(page.Items.Single()).IsEqualTo(proof);
        await platform.AcknowledgeDeletionAsync("fixture:client", "fixture secret", page.Items.Single());
        await Assert.That(handler.Calls).IsEqualTo(2);
        await Assert.That(http.DefaultRequestHeaders.Authorization.Parameter).IsEqualTo("fixture-default");
    }

    private static TerraScaleAuthDeletionProof Proof() => new(Id, "project", Subject, Organization);
    private static string Page(params TerraScaleAuthDeletionProof[] proofs) => JsonSerializer.Serialize(new
    {
        items = proofs.Select(proof => new { id = proof.Id, scope = proof.Scope, subjectId = proof.SubjectId, organizationId = proof.OrganizationId })
    });
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
    private static Task Call(TerraScaleAuthPlatformClient platform, bool acknowledge, CancellationToken ct = default) => acknowledge
        ? platform.AcknowledgeDeletionAsync("fixture", "fixture-secret", Proof(), ct)
        : platform.ListDeletionsAsync("fixture", "fixture-secret", ct);
    private static TerraScaleAuthClient Client(HttpClient http) => new(http, new TerraScaleAuthClientOptions
    {
        BaseAddress = new Uri("https://auth.example.test"),
        InstanceSlug = "private-instance",
        ApiKey = "fixture-management",
    });
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }
}
