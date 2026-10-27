using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

// docs/terrabase/database-rename.md: names are unique per project after convergence; concurrent cross-region claims resolve
// to one winner. Each race holds peer delivery until BOTH regions committed their own decision locally, then heals.
public sealed partial class DatabaseRenameRaceTests
{
    [Test]
    public async Task Rename_and_create_of_one_name_in_two_regions_both_accept_and_converge_to_the_smallest_ulid()
    {
        var (regions, renamed) = await StartAsync("left");
        await using var cleanup = regions;
        var (gru, sjc) = (regions.Gru, regions.Sjc);
        var (backups, policy) = await PrepareAsync(gru, renamed);
        await AssertConvergedAsync(regions, renamed.Project, [renamed]);

        regions.Delivery.Arm(renamed.Project);
        using var a = Client(gru, renamed.Project);
        using var b = Client(sjc, renamed.Project);
        var rename = a.PatchAsJsonAsync(Uri(ById(renamed).Data), new { name = "shared" });
        var create = b.PostAsJsonAsync(Uri($"/v1/projects/{renamed.Project}/databases"), new { name = "shared", allRegions = true });
        string? createdId = null;
        try
        {
            await UntilAsync(async () => await NamedLocallyAsync(gru, renamed.Project, renamed.Id, "shared")
                && await ReadCatalogAsync(sjc, $"projects/{renamed.Project}/database_names", "shared") is { } claim
                && JsonSerializer.Deserialize(claim, TerraBaseJsonContext.Default.String) is { } id && id != renamed.Id
                && await NamedLocallyAsync(sjc, renamed.Project, createdId = id, "shared"));
        }
        finally
        {
            regions.Delivery.Release();
        }

        using var renameResponse = await rename;
        using var createResponse = await create;
        await Assert.That(renameResponse.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await renameResponse.Content.ReadAsStringAsync());
        await Assert.That(createResponse.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await createResponse.Content.ReadAsStringAsync());
        var created = new ManagedDatabaseRef(renamed.Organization, renamed.Project, createdId!, "shared");
        await Assert.That((await createResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()).IsEqualTo(created.Id);
        await PopulateAsync(sjc, created);

        var (winner, loser) = string.CompareOrdinal(renamed.Id, created.Id) < 0 ? (renamed, created) : (created, renamed);
        await AssertConflictResolvedAsync(regions, winner, loser, "shared");
        await AssertMissingAsync(regions.All, renamed.Project, "left");
        await Assert.That(await gru.Client.GetStringAsync(Uri(renamed.Path + "/backups"))).IsEqualTo(backups);
        await Assert.That(await gru.Client.GetStringAsync(Uri(renamed.Path + "/backup-policy"))).IsEqualTo(policy);
        foreach (var region in regions.All)
        {
            await Assert.That(await EventsAsync(region, renamed.Organization, "database.renamed", renamed.Id)).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Renames_of_two_databases_to_one_name_in_two_regions_both_accept_and_the_loser_keeps_everything()
    {
        var (regions, left) = await StartAsync("left");
        await using var cleanup = regions;
        var (gru, sjc) = (regions.Gru, regions.Sjc);
        var right = await sjc.CreateManagedDatabaseAsync(left.Organization, left.Project, "right");
        await UntilAsync(async () => await ReadCatalogAsync(gru, $"projects/{left.Project}/databases", right.Id) is not null);
        var backups = new[] { (gru, left, await PrepareAsync(gru, left)), (sjc, right, await PrepareAsync(sjc, right)) };
        await AssertConvergedAsync(regions, left.Project, [left, right]);

        regions.Delivery.Arm(left.Project);
        using var a = Client(gru, left.Project);
        using var b = Client(sjc, left.Project);
        var first = a.PatchAsJsonAsync(Uri(ById(left).Data), new { name = "shared" });
        var second = b.PatchAsJsonAsync(Uri(ById(right).Data), new { name = "shared" });
        try
        {
            await UntilAsync(async () => await NamedLocallyAsync(gru, left.Project, left.Id, "shared") && await NamedLocallyAsync(sjc, left.Project, right.Id, "shared"));
        }
        finally
        {
            regions.Delivery.Release();
        }

        using var firstResponse = await first;
        using var secondResponse = await second;
        await Assert.That(firstResponse.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await firstResponse.Content.ReadAsStringAsync());
        await Assert.That(secondResponse.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await secondResponse.Content.ReadAsStringAsync());

        var (winner, loser) = string.CompareOrdinal(left.Id, right.Id) < 0 ? (left, right) : (right, left);
        await AssertConflictResolvedAsync(regions, winner, loser, "shared");
        await AssertMissingAsync(regions.All, left.Project, "left", "right");
        foreach (var (home, database, (list, policy)) in backups)
        {
            await Assert.That(await home.Client.GetStringAsync(Uri(database.Path + "/backups"))).IsEqualTo(list);
            await Assert.That(await home.Client.GetStringAsync(Uri(database.Path + "/backup-policy"))).IsEqualTo(policy);
        }

        foreach (var region in regions.All)
        {
            foreach (var database in new[] { left, right })
            {
                await Assert.That(await EventsAsync(region, left.Organization, "database.renamed", database.Id)).IsEqualTo(1);
            }
        }

        // The customer then names the loser: the conflict banner clears and its conflict name is released everywhere.
        var loserHome = loser == left ? gru : sjc;
        using var owner = Client(loserHome, left.Project);
        using (var recovered = await owner.PatchAsJsonAsync(Uri(ById(loser).Data), new { name = "recovered" }))
        {
            await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await recovered.Content.ReadAsStringAsync());
        }

        var labels = await AssertConvergedAsync(regions, left.Project, [winner, loser]);
        await Assert.That(labels[winner.Id]).IsEqualTo("shared");
        await Assert.That(labels[loser.Id]).IsEqualTo("recovered");
        await AssertMissingAsync(regions.All, left.Project, ConflictName("shared", loser.Id));
        foreach (var region in regions.All)
        {
            var body = await BodyAsync(region, loser);
            await Assert.That(Field(body, "previousName")).IsNull();
            await Assert.That(Field(body, "conflictOf")).IsNull();
        }
    }

    [Test]
    public async Task Competing_historical_names_of_one_database_converge_to_one_name_and_drop_every_other_alias()
    {
        var (regions, database) = await StartAsync("original");
        await using var cleanup = regions;
        var (gru, sjc) = (regions.Gru, regions.Sjc);
        await PrepareAsync(gru, database);
        await AssertConvergedAsync(regions, database.Project, [database]);
        using var a = Client(gru, database.Project);
        using var b = Client(sjc, database.Project);
        using (var wrongHome = await b.PatchAsJsonAsync(Uri(ById(database).Data), new { name = "impossible" }))
        {
            await Assert.That(wrongHome.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
        }

        // gru, the home, renames over HTTP. The sjc decision is INJECTED: current HTTP refuses it (421 above), so it stands
        // for a historical durable decision of a stale home, written as the same label-only catalog transaction a rename
        // writes. CDC, peer delivery and convergence of both stay real.
        regions.Delivery.Arm(database.Project);
        Task<HttpResponseMessage> rename;
        try
        {
            rename = a.PatchAsJsonAsync(Uri(ById(database).Data), new { name = "first" });
            await InjectHistoricalRenameAsync(sjc, database, "second");
            await UntilAsync(async () => await NamedLocallyAsync(gru, database.Project, database.Id, "first") && await NamedLocallyAsync(sjc, database.Project, database.Id, "second"));
        }
        finally
        {
            regions.Delivery.Release();
        }

        using (var renamed = await rename)
        {
            await Assert.That(renamed.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await renamed.Content.ReadAsStringAsync());
        }

        var labels = await AssertConvergedAsync(regions, database.Project, [database]);
        var kept = labels[database.Id];
        await Assert.That(kept is "first" or "second").IsTrue().Because(kept);
        await AssertMissingAsync(regions.All, database.Project, "original", "impossible", kept == "first" ? "second" : "first");
    }

    [Test]
    public async Task Rename_racing_delete_keeps_purge_authority_for_the_deleted_database_and_never_deletes_the_claimant()
    {
        var (regions, target) = await StartAsync("original");
        await using var cleanup = regions;
        var (gru, sjc) = (regions.Gru, regions.Sjc);
        var unrelated = await gru.CreateManagedDatabaseAsync(target.Organization, target.Project, "unrelated");
        await PrepareAsync(gru, target);
        var (backups, _) = await PrepareAsync(gru, unrelated);
        await AssertConvergedAsync(regions, target.Project, [target, unrelated]);

        // At its home, gru renames the target and then deletes it; meanwhile sjc, which sees neither, creates a database
        // under the target's new name. Delivery is held until all three decisions committed locally.
        regions.Delivery.Arm(target.Project);
        using var a = Client(gru, target.Project);
        using var b = Client(sjc, target.Project);
        Task<HttpResponseMessage> rename, create, delete;
        string? createdId = null;
        try
        {
            rename = a.PatchAsJsonAsync(Uri(ById(target).Data), new { name = "renamed" });
            await UntilAsync(() => NamedLocallyAsync(gru, target.Project, target.Id, "renamed"));
            create = b.PostAsJsonAsync(Uri($"/v1/projects/{target.Project}/databases"), new { name = "renamed", allRegions = true });
            await UntilAsync(async () => await ReadCatalogAsync(sjc, $"projects/{target.Project}/database_names", "renamed") is { } claim
                && JsonSerializer.Deserialize(claim, TerraBaseJsonContext.Default.String) is { } id && id != target.Id
                && await NamedLocallyAsync(sjc, target.Project, createdId = id, "renamed"));
            delete = a.DeleteAsync(Uri(ById(target).Data));
            await UntilAsync(async () => await ReadCatalogAsync(gru, DatabasePurge.Collection, target.Id) is not null
                && await ReadCatalogAsync(gru, $"projects/{target.Project}/databases", target.Id) is null);
        }
        finally
        {
            regions.Delivery.Release();
        }

        using (var renamed = await rename)
        {
            await Assert.That(renamed.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await renamed.Content.ReadAsStringAsync());
        }

        using (var created = await create)
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync());
        }

        using (var deleted = await delete)
        {
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await deleted.Content.ReadAsStringAsync());
        }

        var claimant = new ManagedDatabaseRef(target.Organization, target.Project, createdId!, "renamed");
        await PopulateAsync(sjc, claimant);
        // Legitimate deletion: the target's purge authority is recorded in every region and nothing of it answers. Conflict
        // loss would instead show as purge authority, a cleanup intent or a missing file for the claimant or the bystander;
        // AssertConvergedAsync refuses all three.
        var labels = await AssertConvergedAsync(regions, target.Project, [unrelated, claimant], [target]);
        await AssertMissingAsync(regions.All, target.Project, "original");
        foreach (var region in regions.All)
        {
            await Assert.That(await ReadCatalogAsync(region, DatabasePurge.Collection, target.Id)).IsNotNull();
            await Assert.That(await ReadCatalogAsync(region, DatabasePurge.Collection, claimant.Id)).IsNull();
        }

        // Peer delivery may join the target's rename before its deletion, so the claimant may briefly lose to the smaller
        // target ULID: it then keeps its conflict name, with one event, rather than its claim. Either way it is never deleted.
        var name = labels[claimant.Id];
        var lost = name != "renamed";
        if (lost)
        {
            await Assert.That(name).IsEqualTo(ConflictName("renamed", claimant.Id));
            await AssertMissingAsync(regions.All, target.Project, "renamed");
        }

        foreach (var region in regions.All)
        {
            await Assert.That(await EventsAsync(region, target.Organization, "database.name_conflict", claimant.Id)).IsEqualTo(lost ? 1 : 0);
        }

        await Assert.That(await gru.Client.GetStringAsync(Uri(unrelated.Path + "/backups"))).IsEqualTo(backups);
    }

    // After convergence: exactly one database holds `name`, the smallest ULID, in every region; the other keeps its ULID, data,
    // file and ULID-restricted key under its deterministic conflict name with the conflict banner and one audit event.
    private static async Task AssertConflictResolvedAsync(Regions regions, ManagedDatabaseRef winner, ManagedDatabaseRef loser, string name)
    {
        var labels = await AssertConvergedAsync(regions, winner.Project, [winner, loser]);
        await Assert.That(labels.Values.Count(label => label == name)).IsEqualTo(1);
        await Assert.That(labels[winner.Id]).IsEqualTo(name);
        await Assert.That(labels[loser.Id]).IsEqualTo(ConflictName(name, loser.Id));
        foreach (var region in regions.All)
        {
            using var client = Client(region, winner.Project);
            using var byName = await client.GetAsync(Uri($"/v1/projects/{winner.Project}/databases/{name}"));
            await Assert.That((await byName.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()).IsEqualTo(winner.Id);
            var body = await BodyAsync(region, loser);
            await Assert.That(Field(body, "previousName")).IsEqualTo(name);
            await Assert.That(Field(body, "conflictOf")).IsEqualTo(winner.Id);
            await Assert.That(await EventsAsync(region, winner.Organization, "database.name_conflict", loser.Id)).IsEqualTo(1);
            await Assert.That(await EventsAsync(region, winner.Organization, "database.name_conflict", winner.Id)).IsEqualTo(0);
        }
    }

    // One historical home decision, as the rename transaction writes it: the label fields only, the old index entry
    // released only while it is this database's, and the new entry claimed.
    private static async Task InjectHistoricalRenameAsync(ManagementApi api, ManagedDatabaseRef database, string name)
    {
        var catalog = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using var write = await catalog.BeginTransactionAsync(write: true, CancellationToken.None);
        var (records, names) = ($"projects/{database.Project}/databases", $"projects/{database.Project}/database_names");
        var current = EntityPayload.Read((await write.GetAsync(records, database.Id, CancellationToken.None))!, TerraBaseJsonContext.Default.ManagedDatabase)!;
        if (await write.GetAsync(names, current.Name, CancellationToken.None) is { } entry
            && JsonSerializer.Deserialize(entry.Json.Span, TerraBaseJsonContext.Default.String) == database.Id)
        {
            _ = await write.DeleteAsync(names, current.Name, CancellationToken.None);
        }

        await write.PutAsync(names, name, JsonSerializer.SerializeToUtf8Bytes(database.Id, TerraBaseJsonContext.Default.String), CancellationToken.None);
        var renamed = current with { Name = name, PreviousName = null, ConflictOf = null };
        FieldOperation[] fields =
        [
            new(FieldOperationKind.Set, "name", JsonSerializer.SerializeToElement(name, TerraBaseJsonContext.Default.String)),
            new(FieldOperationKind.Unset, "previousName"),
            new(FieldOperationKind.Unset, "conflictOf"),
        ];
        _ = await write.PatchAsync(records, database.Id,
            EntityPayload.WithoutId(JsonSerializer.SerializeToUtf8Bytes(renamed, TerraBaseJsonContext.Default.ManagedDatabase), database.Id, nested: true), fields,
            CancellationToken.None);
        await write.CommitAsync(CancellationToken.None);
    }
}
