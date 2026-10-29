using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase.Shapes;

public sealed class TerraBaseShapeProtocolTests
{
    [Test]
    public async Task EveryOfficialFilterInputIsAcceptedBeforeAnyHttpRequest()
    {
        using var server = new ShapeFakeServer(_ => throw new InvalidOperationException("Validation must not send HTTP."));
        using var http = new HttpClient(server);
        var client = new TerraBaseShapeClient(http, new TerraBaseShapeClientOptions
        {
            BaseAddress = new Uri(ShapeVectors.Origin),
            ApiKey = "tb_fixture"
        });
        foreach (var vector in ShapeVectors.Root.GetProperty("filter_vectors").EnumerateArray())
        {
            var where = vector.GetProperty("input_where_utf8").GetString();
            await Assert.That(() => client.SubscribeAsync(ShapeVectors.Project, ShapeVectors.Database, ShapeVectors.Collection, where))
                .ThrowsNothing().Because(vector.GetProperty("id").GetString()!);
        }
        await Assert.That(server.Requests).IsEmpty();
    }

    [Test]
    public async Task OffsetGrammarMatchesEveryOfficialPositiveAndNegativeVector()
    {
        foreach (var vector in ShapeVectors.Root.GetProperty("offset_vectors").EnumerateArray())
        {
            var offset = vector.GetProperty("value").GetString();
            await Assert.That(TerraBaseShapeProtocol.IsValidOffset(offset))
                .IsEqualTo(vector.GetProperty("valid").GetBoolean()).Because(offset!);
        }
    }
}
