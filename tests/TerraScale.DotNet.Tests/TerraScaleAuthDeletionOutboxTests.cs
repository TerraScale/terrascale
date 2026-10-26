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

    [Test]
    [Arguments(false, 401)]
    [Arguments(false, 404)]
    [Arguments(false, 429)]
    [Arguments(false, 503)]
    [Arguments(false, 204)]
    [Arguments(true, 401)]
    [Arguments(true, 404)]
    [Arguments(true, 409)]
    [Arguments(true, 429)]
    [Arguments(true, 503)]
    [Arguments(true, 200)]
    [Arguments(true, 202)]
    public async Task UnexpectedStatusesThrowAndNeverRetry(bool acknowledge, int status)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<TerraScaleAuthException>(() => Call(Client(http).Platform, acknowledge));
        await Assert.That(failure!.StatusCode).IsEqualTo((HttpStatusCode)status);
        if (status < 300) await Assert.That(failure.ErrorCode).IsEqualTo("invalid_response");
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test]
    [Arguments("{}")]
    [Arguments("null")]
    [Arguments("{\"items\":null}")]
    [Arguments("{\"items\":[null]}")]
    [Arguments("{\"items\":[{}]}")]
    [Arguments("{\"items\":[],\"extra\":true}")]
    [Arguments("{\"items\":[{\"id\":\"ID\",\"scope\":\"account\",\"subjectId\":\"SUBJECT\"}]}")]
    [Arguments("{\"items\":[{\"id\":\"ID\",\"scope\":\"account\",\"subjectId\":\"SUBJECT\",\"organizationId\":null,\"extra\":true}]}")]
    [Arguments("{\"items\":[{\"id\":null,\"scope\":\"account\",\"subjectId\":\"SUBJECT\",\"organizationId\":null}]}")]
    [Arguments("{\"items\":[{\"id\":\"ID\",\"scope\":3,\"subjectId\":\"SUBJECT\",\"organizationId\":null}]}")]
    public async Task MalformedPagesAreRejected(string json)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(json.Replace("ID", Id).Replace("SUBJECT", Subject))));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<TerraScaleAuthException>(() => Client(http).Platform.ListDeletionsAsync("fixture", "fixture-secret"));
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task InvalidScopesIdentitiesAndBindingsFailForBothResponseAndAcknowledgement()
    {
        var valid = new TerraScaleAuthDeletionProof(Id, "account", Subject, null);
        var invalid = new[]
        {
            valid with { Scope = "unknown" }, valid with { Scope = "Account" }, valid with { Scope = null! },
            valid with { Id = "../other" }, valid with { Id = Id.ToLowerInvariant() },
            valid with { Id = "8" + Id[1..] }, valid with { SubjectId = "bad" }, valid with { SubjectId = null! },
            valid with { OrganizationId = Organization }, valid with { Scope = "organization" },
            valid with { Scope = "organization", OrganizationId = Organization },
            valid with { Scope = "project" }, valid with { Scope = "project", OrganizationId = "bad" }
        };
        foreach (var proof in invalid)
        {
            using var handler = new Handler((_, _) => Task.FromResult(Json(Page(proof))));
            using var http = new HttpClient(handler);
            var platform = Client(http).Platform;
            await Assert.ThrowsAsync<ArgumentException>(() => platform.AcknowledgeDeletionAsync("fixture", "fixture-secret", proof));
            await Assert.That(handler.Calls).IsEqualTo(0);
            var failure = await Assert.ThrowsAsync<TerraScaleAuthException>(() => platform.ListDeletionsAsync("fixture", "fixture-secret"));
            await Assert.That(failure!.ErrorCode).IsEqualTo("invalid_response");
            await Assert.That(handler.Calls).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments(0)]
    [Arguments(20)]
    [Arguments(21)]
    public async Task PageLimitIsEnforced(int count)
    {
        var proofs = Enumerable.Range(0, count).Select(_ => Proof()).ToArray();
        using var handler = new Handler((_, _) => Task.FromResult(Json(Page(proofs))));
        using var http = new HttpClient(handler);
        var pending = Client(http).Platform.ListDeletionsAsync("fixture", "fixture-secret");
        if (count > 20) await Assert.ThrowsAsync<TerraScaleAuthException>(() => pending);
        else await Assert.That((await pending).Items.Count).IsEqualTo(count);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TransportFailureIsSentOnceAndCancellationIsPreserved(bool acknowledge)
    {
        using var handler = new Handler((_, _) => throw new HttpRequestException("Synthetic transport failure."));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => Call(Client(http).Platform, acknowledge));
        await Assert.That(handler.Calls).IsEqualTo(1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationHandler = new Handler(async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unreachable.");
        });
        using var cancellationHttp = new HttpClient(cancellationHandler);
        using var cancellation = new CancellationTokenSource();
        var pending = Call(Client(cancellationHttp).Platform, acknowledge, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        await Assert.That(cancellationHandler.Calls).IsEqualTo(1);
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
