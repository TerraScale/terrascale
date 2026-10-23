using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Sql;

public sealed class SqlCatalogTests
{
    private const string Schema = """{"type":"object"}""";

    [Test]
    public async Task A_table_definition_and_a_schema_are_metadata_members_that_never_overwrite_each_other()
    {
        await using var api = await DocumentApi.StartAsync();
        var schemaPath = $"{api.Collections}/{DocumentApi.Collection}/schema";
        (await api.SendRawAsync(HttpMethod.Put, schemaPath, Schema, "application/json")).Status.ShouldBe(HttpStatusCode.OK);
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var tenant = await api.TenantOfAsync();
        using var definition = JsonDocument.Parse("""{"columns":[{"name":"a","type":"TEXT","not_null":false}],"primary_key":[]}""");

        bool setMissing;
        await using (var write = (await engine.BeginTransactionAsync(tenant, write: true, CancellationToken.None))!)
        {
            await Assert.That(await write.SetCollectionTableAsync(DocumentApi.Collection, definition.RootElement, CancellationToken.None)).IsTrue();
            setMissing = await write.SetCollectionTableAsync("absent", definition.RootElement, CancellationToken.None);
            await write.CommitAsync(CancellationToken.None);
        }

        var schemaAfterTable = await api.SendAsync(HttpMethod.Get, schemaPath);
        // Replacing the schema keeps the definition.
        (await api.SendRawAsync(HttpMethod.Put, schemaPath, """{"type":"object","required":[]}""", "application/json")).Status.ShouldBe(HttpStatusCode.OK);
        await using var read = (await engine.BeginTransactionAsync(tenant, write: false, CancellationToken.None))!;
        var stored = await read.GetCollectionTableAsync(DocumentApi.Collection, CancellationToken.None);
        var missing = await read.GetCollectionTableAsync("absent", CancellationToken.None);

        await Assert.That(setMissing).IsFalse();
        await Assert.That(schemaAfterTable.Json.GetProperty("schema").GetRawText()).IsEqualTo(Schema);
        await Assert.That(stored!.Value.GetRawText()).IsEqualTo(definition.RootElement.GetRawText());
        await Assert.That(missing).IsNull();
        await Assert.That((await api.SendAsync(HttpMethod.Get, $"{api.Collections}/{DocumentApi.Collection}")).Status).IsEqualTo(HttpStatusCode.OK);
    }
}
