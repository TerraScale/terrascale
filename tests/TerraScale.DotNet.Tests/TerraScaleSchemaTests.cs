using System.Text.Json;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleSchemaTests
{
    [Test]
    public async Task Builder_EmitsDeterministicSchemaJsonWithIndexesTtlHistoryAndMergePolicy()
    {
        var schema = new TerraScaleSchemaBuilder()
            .Collection("events", collection => collection
                .Field("expires_at", TerraScaleFieldType.Timestamp, required: true)
                .Field("tenant", TerraScaleFieldType.String, required: true, indexed: true)
                .Field("title", TerraScaleFieldType.Text, indexed: true)
                .Field("visits", TerraScaleFieldType.PnCounter, mergePolicy: TerraScaleFieldType.PnCounter)
                .Index("events_by_tenant", "tenant")
                .FullText("events_search", "title")
                .Ttl("expires_at")
                .HistoryRetentionDays(30)
                .MergePolicy(TerraScaleMergePolicy.HlcTiebreak))
            .Build();

        var first = schema.ToJson();
        var second = schema.ToJson();
        await Assert.That(first).IsEqualTo(second);

        using var json = JsonDocument.Parse(first);
        var collection = json.RootElement.GetProperty("collections")[0];
        await Assert.That(collection.GetProperty("name").GetString()).IsEqualTo("events");
        await Assert.That(collection.GetProperty("historyRetentionDays").GetInt32()).IsEqualTo(30);
        await Assert.That(collection.GetProperty("mergePolicy").GetString()).IsEqualTo("hlc-tiebreak");
        await Assert.That(collection.GetProperty("ttl").GetProperty("field").GetString()).IsEqualTo("expires_at");
        await Assert.That(collection.GetProperty("indexes")[1].GetProperty("kind").GetString()).IsEqualTo("full-text");
        await Assert.That(collection.GetProperty("fields")[3].GetProperty("mergePolicy").GetString()).IsEqualTo("counter-pn");
    }

    [Test]
    public async Task Builder_DefaultsHistoryAndSupportsScalarAndCrdtFields()
    {
        var collection = new TerraScaleCollectionBuilder("users")
            .Field("name", TerraScaleFieldType.String)
            .Field("age", TerraScaleFieldType.Int)
            .Field("labels", TerraScaleFieldType.AddWinsSet)
            .Field("removed", TerraScaleFieldType.RemoveWinsSet)
            .Field("versions", TerraScaleFieldType.MvRegister)
            .Build();

        await Assert.That(collection.HistoryRetentionDays).IsEqualTo(7);
        await Assert.That(collection.Fields).Count().IsEqualTo(5);
        await Assert.That(collection.Fields[2].Type).IsEqualTo(TerraScaleFieldType.AddWinsSet);
    }

    [Test]
    public async Task Builder_RejectsInvalidBoundsNamesReferencesAndPolicies()
    {
        var history = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
        {
            new TerraScaleCollectionBuilder("events").HistoryRetentionDays(31).Build();
            return Task.CompletedTask;
        });
        var duplicate = await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            new TerraScaleCollectionBuilder("events")
                .Field("name", TerraScaleFieldType.String)
                .Field("name", TerraScaleFieldType.Int)
                .Build();
            return Task.CompletedTask;
        });
        var ttl = await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            new TerraScaleCollectionBuilder("events")
                .Field("expires_at", TerraScaleFieldType.String)
                .Ttl("expires_at")
                .Build();
            return Task.CompletedTask;
        });
        var index = await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            new TerraScaleCollectionBuilder("events")
                .Index("events_by_missing", "missing")
                .Build();
            return Task.CompletedTask;
        });
        var policy = await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            new TerraScaleCollectionBuilder("events")
                .Field("name", TerraScaleFieldType.String, mergePolicy: TerraScaleFieldType.String)
                .Build();
            return Task.CompletedTask;
        });

        await Assert.That(history).IsNotNull();
        await Assert.That(duplicate).IsNotNull();
        await Assert.That(ttl).IsNotNull();
        await Assert.That(index).IsNotNull();
        await Assert.That(policy).IsNotNull();
    }

    [Test]
    public async Task Schema_ParseAcceptsCrossSdkFieldShape()
    {
        var schema = TerraScaleSchema.Parse("""
            {"collections":[{"name":"users","fields":[{"name":"name","type":"string","required":true,"indexed":true}],"historyRetentionDays":7,"mergePolicy":"remove-wins"}]}
            """);

        await Assert.That(schema.Collections).Count().IsEqualTo(1);
        await Assert.That(schema.Collections[0].Fields[0].Required).IsTrue();
        await Assert.That(schema.Collections[0].Fields[0].Type).IsEqualTo(TerraScaleFieldType.String);
    }
}
