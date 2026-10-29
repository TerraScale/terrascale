using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Testing;

namespace TerraBase.Slow.Management;

public sealed class ManagementRouteIdTests
{
    [Test]
    [Arguments("organization")]
    [Arguments("project")]
    [Arguments("database")]
    [Arguments("query")]
    [Arguments("backup")]
    public async Task Malformed_route_ids_return_the_validation_envelope_without_mutating_the_catalog(string field)
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(cancellationToken);
        var sequence = await SequenceAsync();
        foreach (var invalid in new[] { "missing", "01arz3ndektsv4rrffq69g5fav", "81ARZ3NDEKTSV4RRFFQ69G5FAV", "01ARZ3NDEKTSV4RRFFQ69G5FAI" })
        {
            var (method, path) = field switch
            {
                "organization" => (HttpMethod.Get, $"/v1/management/organizations/{invalid}"),
                "project" => (HttpMethod.Get, $"/v1/management/organizations/{database.Organization}/projects/{invalid}"),
                "database" => (HttpMethod.Delete, $"{database.ProjectPath}/databases/{invalid}"),
                "query" => (HttpMethod.Delete, $"{database.ProjectPath}/saved-queries/{invalid}"),
                "backup" => (HttpMethod.Post, $"{database.Path}/backups/{invalid}/restore"),
                _ => throw new ArgumentOutOfRangeException(nameof(field)),
            };
            using var request = new HttpRequestMessage(method, ManagementApi.Uri(path))
            {
                Content = JsonContent.Create(new { name = "changed" }),
            };
            await ErrorAssert.ErrorAsync(api.Client.SendAsync(request, cancellationToken), HttpStatusCode.BadRequest,
                "invalid_request", field + " must be a canonical uppercase ULID");
        }

        await Assert.That(await SequenceAsync()).IsEqualTo(sequence);
        using var existing = await api.Client.GetAsync(ManagementApi.Uri(database.Path), cancellationToken);
        existing.EnsureSuccessStatusCode();
        await Assert.That((await existing.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("id").GetString()).IsEqualTo(database.Id);

        async Task<long> SequenceAsync()
        {
            // A read transaction's commit answers the latest committed offset.
            await using var transaction = await store.BeginTransactionAsync(write: false, cancellationToken);
            return (await transaction.CommitAsync(cancellationToken)).Offset;
        }
    }

    [Test]
    public async Task Signed_database_names_are_resolved_before_route_validation_and_auth_failures_remain_opaque()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var signed = api.WithToken(api.Issuer.Mint(api.User, token =>
        {
            token.Claims!["project_id"] = database.Project;
            token.Claims!["scope"] = "databases:read databases:write";
        }));
        using var existing = await signed.GetAsync(ManagementApi.Uri(database.Data));
        existing.EnsureSuccessStatusCode();
        await Assert.That((await existing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString()).IsEqualTo(database.Name);
        var invalidQuery = ManagementApi.Uri($"/v1/projects/{database.Project}/saved-queries/malformed");
        await ErrorAssert.ErrorAsync(signed.DeleteAsync(invalidQuery), HttpStatusCode.BadRequest,
            "invalid_request", "query must be a canonical uppercase ULID");
        await ErrorAssert.ErrorAsync(signed.DeleteAsync(ManagementApi.Uri("/v1/projects/malformed/saved-queries/malformed")),
            HttpStatusCode.NotFound, "unknown_database", "the database does not exist");
        using var anonymous = api.WithToken(null);
        await ErrorAssert.ErrorAsync(anonymous.DeleteAsync(invalidQuery), HttpStatusCode.Unauthorized, "invalid_key");
    }
}
