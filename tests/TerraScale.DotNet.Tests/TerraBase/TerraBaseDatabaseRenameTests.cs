using System.Net;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseDatabaseRenameTests
{
    private const string DatabaseId = "01JA0000000000000000000004";
    private const string Renamed = """
        {"id":"01JA0000000000000000000004","projectId":"01JA0000000000000000000001","name":"production",
         "branchedFrom":null,"createdAt":"2026-10-12T00:00:00+00:00","region":"sjc","homeRegion":"gru",
         "replicaRegions":["sjc","fra"],"allRegions":false}
        """;

    [Test]
    [Arguments(DatabaseId)]
    public async Task Rename_sends_one_name_only_patch_and_reads_database_identity_and_placement(string databaseId)
    {
        var requests = 0;
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            requests++;
            await Assert.That(request.Method).IsEqualTo(HttpMethod.Patch);
            await Assert.That(request.RequestUri!.AbsolutePath).IsEqualTo($"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/databases/{databaseId}");
            await Assert.That(request.Headers.Authorization!.ToString()).IsEqualTo("Bearer project-user-token");
            await Assert.That(request.Headers.Contains("X-TerraBase-User-Id")).IsFalse();
            await Assert.That(request.Headers.GetValues("X-TerraScale-Region-Pin").Single()).IsEqualTo("sjc");
            await Assert.That(request.Content!.Headers.ContentType!.MediaType).IsEqualTo("application/json");
            await Assert.That(await request.Content.ReadAsStringAsync(token)).IsEqualTo("{\"name\":\"production\"}");
            return TerraBaseIntegrationFixture.Json(Renamed);
        });
        using var http = new HttpClient(handler);

        var result = await TerraBaseIntegrationFixture.Client(http).Project(TerraBaseIntegrationFixture.ProjectId)
            .RenameDatabaseAsync(databaseId, "production");

        await Assert.That(requests).IsEqualTo(1);
        await Assert.That(result.Id).IsEqualTo(DatabaseId);
        await Assert.That(result.ProjectId).IsEqualTo(TerraBaseIntegrationFixture.ProjectId);
        await Assert.That(result.Name).IsEqualTo("production");
        await Assert.That(result.Region).IsEqualTo("sjc");
        await Assert.That(result.HomeRegion).IsEqualTo("gru");
        await Assert.That(result.ReplicaRegions).IsEquivalentTo(["sjc", "fra"]);
        await Assert.That(result.AllRegions).IsFalse();
    }

    [Test]
    public async Task Reading_by_id_exposes_the_conflict_name_and_winner_without_changing_identity()
    {
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            if (request.Method != HttpMethod.Get || !request.RequestUri!.AbsolutePath.EndsWith("/" + DatabaseId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("expected one metadata read by immutable id");
            }
            return Task.FromResult(TerraBaseIntegrationFixture.Json(Renamed.Replace("\"name\":\"production\"",
                "\"name\":\"production-conflict-000004\",\"previousName\":\"production\",\"conflictOf\":\"01JA0000000000000000000003\"", StringComparison.Ordinal)));
        });
        using var http = new HttpClient(handler);
        var result = await TerraBaseIntegrationFixture.Client(http).Project(TerraBaseIntegrationFixture.ProjectId).GetDatabaseAsync(DatabaseId);
        await Assert.That(result.Id).IsEqualTo(DatabaseId);
        await Assert.That(result.Name).IsEqualTo("production-conflict-000004");
        await Assert.That(result.PreviousName).IsEqualTo("production");
        await Assert.That(result.ConflictOf).IsEqualTo("01JA0000000000000000000003");
        await Assert.That(result.HomeRegion).IsEqualTo("gru");
    }

    [Test]
    [Arguments(400, typeof(TerraBaseValidationException), "invalid_request")]
    [Arguments(403, typeof(TerraBaseForbiddenException), "forbidden_scope")]
    [Arguments(404, typeof(TerraBaseNotFoundException), "not_found")]
    [Arguments(409, typeof(TerraBaseConflictException), "name_taken")]
    [Arguments(421, typeof(TerraBaseWrongRegionException), "wrong_region")]
    [Arguments(503, typeof(TerraBaseUnavailableException), "storage_unavailable")]
    public async Task Rename_preserves_server_errors_without_retry(int status, Type expected, string code)
    {
        var requests = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(TerraBaseIntegrationFixture.Json(
                $$$"""{"error":{"code":"{{{code}}}","message":"rename refused"}}""", (HttpStatusCode)status));
        });
        using var http = new HttpClient(handler);
        var project = TerraBaseIntegrationFixture.Client(http).Project(TerraBaseIntegrationFixture.ProjectId);

        var error = await Assert.ThrowsAsync<TerraBaseException>(() => project.RenameDatabaseAsync(DatabaseId, "production"));

        await Assert.That(error!.GetType()).IsEqualTo(expected);
        await Assert.That(error.StatusCode).IsEqualTo((HttpStatusCode)status);
        await Assert.That(error.ErrorCode).IsEqualTo(code);
        await Assert.That(requests).IsEqualTo(1);
    }

    [Test]
    [Arguments("app")]
    [Arguments("01ja0000000000000000000004")]
    public async Task Rename_rejects_noncanonical_database_ids_before_sending(string databaseId)
    {
        using var handler = new TerraBaseTestHandler((_, _) => throw new InvalidOperationException("unexpected request"));
        using var http = new HttpClient(handler);
        var project = TerraBaseIntegrationFixture.Client(http).Project(TerraBaseIntegrationFixture.ProjectId);
        await Assert.ThrowsAsync<ArgumentException>(() => project.RenameDatabaseAsync(databaseId, "production"));
    }

    [Test]
    public async Task Rename_honors_caller_cancellation_during_the_request()
    {
        using var caller = new CancellationTokenSource();
        var requests = 0;
        using var handler = new TerraBaseTestHandler((_, token) =>
        {
            requests++;
            caller.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(token);
        });
        using var http = new HttpClient(handler);
        var project = TerraBaseIntegrationFixture.Client(http).Project(TerraBaseIntegrationFixture.ProjectId);

        await Assert.ThrowsAsync<OperationCanceledException>(() => project.RenameDatabaseAsync(DatabaseId, "production", caller.Token));
        await Assert.That(requests).IsEqualTo(1);
    }
}
