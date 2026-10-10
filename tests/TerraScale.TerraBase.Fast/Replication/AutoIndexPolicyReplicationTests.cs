using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class AutoIndexPolicyReplicationTests
{
    [Test]
    public async Task Opt_out_reenabling_and_database_deletion_replicate_to_the_followers_local_catalog()
    {
        using var peers = new PeerTls();
        await using var primary = await ManagementApi.StartAsync(peers.Apply);
        var database = await primary.CreateManagedDatabaseAsync();
        await primary.VerifyProjectBindingAsync(database.Organization, database.Project);
        var collection = "c_" + new Faker().Random.String2(8, "abcdefghijklmnopqrstuvwxyz");
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var follower = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            settings["catalog-primary"] = primary.Server.PeerAddress!.ToString();
        });
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            using var readiness = await follower.Client.GetAsync(ManagementApi.Uri("/readyz"), token);
            return readiness.StatusCode == HttpStatusCode.OK;
        });

        var path = ManagementApi.Uri(
            $"/v1/projects/{database.Project}/databases/{database.Name}/auto-index/{collection}");
        using var policyClient = primary.WithToken(primary.Issuer.Mint(primary.User, token =>
        {
            token.Claims["project_id"] = database.Project;
            token.Claims["scope"] = "databases:read databases:write";
        }));
        await AssertPolicyResponseAsync(policyClient.GetAsync(path, cancellationToken), collection, true, cancellationToken);
        await AssertStoredOptOutAsync(follower, database, collection, present: false);

        foreach (var enabled in new[] { false, true, false })
        {
            await AssertPolicyResponseAsync(
                policyClient.PutAsJsonAsync(path, new { enabled }, cancellationToken), collection, enabled, cancellationToken);
            await AssertStoredOptOutAsync(follower, database, collection, present: !enabled);
        }

        using var removed = await primary.Client.DeleteAsync(ManagementApi.Uri(database.Path), cancellationToken);
        await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await AssertStoredOptOutAsync(follower, database, collection, present: false);
    }

    private static async Task AssertPolicyResponseAsync(
        Task<HttpResponseMessage> sending, string collection, bool enabled, CancellationToken cancellationToken)
    {
        using var response = await sending;
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var policy = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        await Assert.That(policy.GetProperty("collection").GetString()).IsEqualTo(collection);
        await Assert.That(policy.GetProperty("enabled").GetBoolean()).IsEqualTo(enabled);
    }

    private static async Task AssertStoredOptOutAsync(
        TerraBaseTestServer follower, ManagedDatabaseRef database, string collection, bool present)
    {
        StoredDocument? stored = null;
        // Follower management GETs forward to the primary, so only its own engine proves replication.
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            var store = await follower.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
            await using var transaction = await store.BeginTransactionAsync(write: false, token);
            var records = $"databases/{database.Id}/auto_index_policies";
            stored = await transaction.GetAsync(records, collection, token);
            var count = await transaction.CountDocumentsAsync(records, token);
            return (stored is not null) == present && count == (present ? 1 : 0);
        });

        if (stored is { } document)
        {
            using var policy = JsonDocument.Parse(document.Json);
            await Assert.That(document.Id).IsEqualTo(collection);
            await Assert.That(policy.RootElement.GetProperty("collection").GetString()).IsEqualTo(collection);
            await Assert.That(policy.RootElement.GetProperty("enabled").GetBoolean()).IsFalse();
        }
        else
        {
            await Assert.That(stored).IsNull();
        }
    }
}
