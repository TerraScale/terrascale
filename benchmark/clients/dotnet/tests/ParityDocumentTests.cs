using System.Text;
using ApiBench;
using TerraScale.DotNet.TerraBase;
using TUnit.Core;

namespace BenchClient.Tests;

public sealed class ParityDocumentTests
{
    private const string Id = "01ARZ3NDEKTSV4RRFFQ69G5FAA";
    private const string Version = "seed-version";

    [Test]
    [Arguments(256)]
    [Arguments(4096)]
    public async Task BothLanesAcceptTheExactSeedAtItsRequiredByteSize(int size)
    {
        var body = Payload.Json(0, size);
        var expected = new ParityDocument(Id, Version, body);
        await Assert.That(Encoding.UTF8.GetByteCount(body)).IsEqualTo(size);
        await Assert.That(expected.Matches(new TerraBaseDocument(Id, Version, "value", Payload.Parse(body)))).IsTrue();
        var raw = Encoding.UTF8.GetBytes($$"""{"id":"{{Id}}","version":"{{Version}}","state":"value","document":{{body}}}!""");
        // A trailing non-JSON byte must fail even after an otherwise valid document.
        await Assert.That(expected.Matches(raw)).IsFalse();
        raw = Encoding.UTF8.GetBytes($$"""{"id":"{{Id}}","version":"{{Version}}","state":"value","document":{{body}}}""");
        await Assert.That(expected.Matches(raw)).IsTrue();
    }

    [Test]
    [Arguments("id")]
    [Arguments("version")]
    [Arguments("state")]
    [Arguments("body")]
    [Arguments("missing-state")]
    [Arguments("missing-document")]
    [Arguments("null")]
    [Arguments("malformed")]
    public async Task AReadMustMatchEverySeedFieldAndHaveAnExplicitValueState(string variant)
    {
        var body = Payload.Json(0, 256);
        var wire = $$"""{"id":"{{Id}}","version":"{{Version}}","state":"value","document":{{body}}}""";
        wire = variant switch
        {
            "id" => wire.Replace(Id, "01ARZ3NDEKTSV4RRFFQ69G5FAB", StringComparison.Ordinal),
            "version" => wire.Replace(Version, "different-version", StringComparison.Ordinal),
            "state" => wire.Replace("\"value\"", "\"deleted\"", StringComparison.Ordinal),
            "body" => wire.Replace('x', 'y'),
            "missing-state" => wire.Replace("\"state\":\"value\",", "", StringComparison.Ordinal),
            "missing-document" => $$"""{"id":"{{Id}}","version":"{{Version}}","state":"value"}""",
            "null" => "null",
            _ => "{",
        };
        await Assert.That(new ParityDocument(Id, Version, body).Matches(Encoding.UTF8.GetBytes(wire))).IsFalse();
    }
}
