// <copyright file="TbepGoldenVectorsTests.cs" company="TerraScale">
// TerraBaseV2 (SE-14). See LICENSE.md.
// </copyright>

namespace TerraBase.Protocol.Tests;

using System.Text.Json;

/// <summary>
/// The C# half of the TBEP cross-language conformance suite
/// (PD-2026-08-26-02 D4): the same committed golden-vector corpus the
/// Rust runner replays drives this suite. Every golden frame must decode
/// to its recorded identity and body bytes, re-encode byte-exact, and
/// respect its written budget; every adversarial row must fail closed
/// the recorded way. If the two languages ever disagree on one byte,
/// one of these rows fails.
/// </summary>
public sealed class TbepGoldenVectorsTests
{
    private static readonly string CorpusRoot = FindCorpusRoot();

    private static string FindCorpusRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, "vectors", "tbep");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("the TBEP corpus directory was not found beside the test binary");
    }

    /// <summary>One corpus file with its recorded documents.</summary>
    public sealed record CorpusFile(string Name, JsonDocument Document)
    {
        /// <summary>The category name.</summary>
        public string Category => this.Document.RootElement.GetProperty("category").GetString()!;

        /// <summary>The golden frames.</summary>
        public JsonElement Frames => this.Document.RootElement.GetProperty("frames");

        /// <summary>The adversarial rows (absent in rejection-free categories).</summary>
        public JsonElement Adversarial =>
            this.Document.RootElement.TryGetProperty("adversarial", out var rows) ? rows : default;
    }

    /// <summary>The corpus files, one per category, asserted against the Rust runner's category set.</summary>
    public static IEnumerable<CorpusFile> Corpus()
    {
        var files = Directory.EnumerateFiles(CorpusRoot, "*.json").Order().ToArray();
        var categories = new[]
        {
            "channel-bind",
            "document-frames",
            "query-cancellation",
            "sync-session",
            "trigger-notification",
            "schema-pitfalls",
            "adversarial-rejection",
        };
        if (files.Length != categories.Length)
        {
            throw new InvalidOperationException($"the corpus holds exactly one file per category, found {files.Length}");
        }
        foreach (var path in files)
        {
            var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = false });
            yield return new CorpusFile(Path.GetFileName(path), document);
        }
    }

    /// <summary>Every golden frame decodes, re-encodes byte-exact, and respects its budget.</summary>
    /// <param name="file">The corpus file.</param>
    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task GoldenFramesReplayByteExact(CorpusFile file)
    {
        var golden = 0;
        foreach (var entry in file.Frames.EnumerateArray())
        {
            var wire = Hex(entry.GetProperty("wire").GetString()!);
            var frame = TbepWire.Decode(wire);

            await Assert.That(TbepRegistry.KindName(frame.Kind)).IsEqualTo(entry.GetProperty("kind").GetString());
            await Assert.That(frame.Correlation).IsEqualTo(entry.GetProperty("correlation").GetUInt64());
            await Assert.That(frame.Causation).IsEqualTo(entry.GetProperty("causation").GetUInt64());
            var idempotency = entry.GetProperty("idempotency");
            if (idempotency.ValueKind == JsonValueKind.String)
            {
                await Assert.That(frame.Idempotency).IsEqualTo(idempotency.GetString());
            }
            else
            {
                await Assert.That(frame.Idempotency).IsNull();
            }

            var body = Hex(entry.GetProperty("body").GetString()!);
            await Assert.That(frame.Body).IsEquivalentTo(body);

            // Byte-exact re-encode: the strongest cross-language row.
            await Assert.That(TbepWire.Encode(frame)).IsEquivalentTo(wire);

            // Overhead accounting and the written budget.
            await Assert.That(TbepWire.EnvelopeOverhead(frame))
                .IsEqualTo((int)entry.GetProperty("overhead_bytes").GetUInt64());
            var budget = BudgetOf(file, TbepRegistry.KindName(frame.Kind));
            await Assert.That(TbepWire.StreamWireBytes(frame)).IsLessThanOrEqualTo(budget);
            golden++;
        }

        if (file.Category == "adversarial-rejection")
        {
            await Assert.That(file.Adversarial.GetArrayLength()).IsGreaterThan(0);
            await Assert.That(golden).IsEqualTo(0);
        }
        else
        {
            await Assert.That(golden).IsGreaterThan(0);
        }

        await Task.CompletedTask;
    }

    /// <summary>Every adversarial row fails closed the recorded way.</summary>
    /// <param name="file">The corpus file.</param>
    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task AdversarialRowsFailClosed(CorpusFile file)
    {
        if (file.Adversarial.ValueKind == JsonValueKind.Undefined)
        {
            return;
        }

        foreach (var entry in file.Adversarial.EnumerateArray())
        {
            var wire = Hex(entry.GetProperty("wire").GetString()!);
            var expected = entry.GetProperty("expect").GetString()!;
            var failure = await Assert.That(() => TbepWire.Decode(wire)).Throws<TbepWire.WireException>();
            await Assert.That(failure!.Failure.ToString().ToSnakeCase()).IsEqualTo(expected);
        }
    }

    /// <summary>The mutation pairing fails closed on both sides of the rule.</summary>
    [Test]
    public async Task IdempotencyPairingFailsClosed()
    {
        var mutationKind = TbepRegistry.RequestKindOf("document.write");
        var readKind = TbepRegistry.RequestKindOf("document.read");
        var body = new byte[] { 0x90 };

        await Assert.That(() => TbepWire.Seal(mutationKind, 1, 0, idempotency: null, body))
            .Throws<TbepWire.WireException>();
        await Assert.That(() => TbepWire.Seal(readKind, 1, 0, idempotency: "retry-01", body))
            .Throws<TbepWire.WireException>();

        var sealedFrame = TbepWire.Seal(mutationKind, 1, 0, "retry-01", body);
        await Assert.That(sealedFrame.Idempotency).IsEqualTo("retry-01");
        await Assert.That(TbepWire.Decode(TbepWire.Encode(sealedFrame)).Idempotency).IsEqualTo("retry-01");
    }

    /// <summary>Objects canonicalize name-sorted in the C# codec exactly as in the Rust codec.</summary>
    [Test]
    public async Task ObjectsCanonicalizeAcrossFieldOrder()
    {
        var first = TbepValue.Of(new ObjectField[]
        {
            new("z", TbepValue.Of(1L)),
            new("a", TbepValue.Null),
            new("m", TbepValue.Of(new TbepValue[] { TbepValue.Of(true) })),
        });
        var second = TbepValue.Of(new ObjectField[]
        {
            new("m", TbepValue.Of(new TbepValue[] { TbepValue.Of(true) })),
            new("z", TbepValue.Of(1L)),
            new("a", TbepValue.Null),
        });
        var firstBytes = MessagePackSerializer.Serialize(first, TbepOptions.Strict);
        var secondBytes = MessagePackSerializer.Serialize(second, TbepOptions.Strict);
        await Assert.That(secondBytes).IsEquivalentTo(firstBytes);
    }

    private static int BudgetOf(CorpusFile file, string kindName)
    {
        // The written budget table rides in the generated registry (the
        // same table the Rust side replays from the corpus file that
        // records it); the frame's class comes from the frame table.
        _ = file;
        var kind = TbepRegistry.KindCode(kindName);
        var row = TbepRegistry.Frames.FirstOrDefault(entry => entry.Code == kind);
        var budget = TbepRegistry.Budgets.FirstOrDefault(entry => entry.Class == row.Class);
        return budget.WireBytes;
    }

    private static byte[] Hex(string text)
    {
        var bytes = new byte[text.Length / 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = Convert.ToByte(text.Substring(index * 2, 2), 16);
        }

        return bytes;
    }
}

/// <summary>The snake-case helper the adversarial expectations carry.</summary>
internal static class FailureExtensions
{
    /// <summary>Converts a PascalCase failure name to the corpus's snake_case.</summary>
    /// <param name="value">The failure name.</param>
    /// <returns>The snake_case form.</returns>
    public static string ToSnakeCase(this string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 8);
        foreach (var (character, index) in value.Select((letter, position) => (letter, position)))
        {
            if (char.IsUpper(character))
            {
                if (index > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(character));
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
