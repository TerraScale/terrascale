using System.Net;
using System.Text;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase.Sql;

public sealed class TerraBaseSqlExportTests
{
    private const string Project = TerraBaseIntegrationFixture.ProjectId;

    [Test]
    public async Task Export_database_streams_the_sql_dump_from_the_home_region()
    {
        var dump = "BEGIN;\nINSERT INTO \"todos\" VALUES (1,'ship');\nCOMMIT;"u8.ToArray();
        HttpRequestMessage? sent = null;
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            sent = request;
            var content = new ByteArrayContent(dump);
            content.Headers.ContentType = new("application/sql");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var http = new HttpClient(handler);
        var client = new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions
        {
            BaseAddress = new Uri("https://terrabase.example.test/"),
            AccessTokenProvider = _ => ValueTask.FromResult("tb_test_key"),
        });

        using var stream = await client.Database(Project, "app").ExportDatabaseAsync();
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);

        await Assert.That(sent!.RequestUri!.AbsolutePath).IsEqualTo($"/v1/projects/{Project}/databases/app/export");
        await Assert.That(sent.Method).IsEqualTo(HttpMethod.Get);
        await Assert.That(string.Join(',', sent.Headers.Accept.Select(accept => accept.MediaType))).IsEqualTo("text/plain");
        await Assert.That(copy.ToArray()).IsEquivalentTo(dump);
    }
}
