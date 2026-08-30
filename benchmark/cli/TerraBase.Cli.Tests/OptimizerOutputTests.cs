// <copyright file="OptimizerOutputTests.cs" company="TerraScale">
// TerraBase (IX-04). See LICENSE.md.
// </copyright>

namespace TerraBase.Cli.Tests;

using System.Text;
using System.Text.Json;

/// <summary>
/// The optimizer-selection output rows (IX-04): the CLI's rendering of the
/// engine's Index Selection Records in both conventions. No engine and no
/// host is needed — the rows pin the writer's own contract: the machine
/// envelope's payload carries the host's values verbatim, and the human
/// convention labels a selection the host reported without a reason as
/// absent rather than inventing one.
/// </summary>
public sealed class OptimizerOutputTests
{
    private static OutputWriter Writer(StringBuilder stdout, string format = "human") => new(
        new CliConfiguration(
            Mode: "host",
            Url: CliConfiguration.DefaultUrl,
            SocketPath: CliConfiguration.DefaultSocketPath,
            Principal: CliConfiguration.DefaultPrincipal,
            Tenant: CliConfiguration.DefaultTenant,
            OutputFormat: format),
        new StringWriter(stdout),
        new StringWriter(new StringBuilder()));

    [Test]
    public async Task HumanConventionLabelsEachSelectionAndCountsTheUnexplained()
    {
        var stdout = new StringBuilder();
        var writer = Writer(stdout);
        var selections = new[]
        {
            new OutputWriter.IndexSelectionLine("Auto/users/ByName", "chosen", "a better match was available"),
            new OutputWriter.IndexSelectionLine("Auto/users/ByAge", "created", null),
        };

        writer.OptimizerSelections("indexes", selections);
        var text = stdout.ToString();

        await Assert.That(text).Contains("Optimizer selections");
        await Assert.That(text).Contains("Auto/users/ByName");
        await Assert.That(text).Contains("chosen");
        await Assert.That(text).Contains("a better match was available");
        await Assert.That(text).Contains("no recorded reason");
        await Assert.That(text).Contains("count 2  without reason 1");
    }

    [Test]
    public async Task MachineConventionCarriesTheHostValuesAndTheUnexplainedCount()
    {
        var stdout = new StringBuilder();
        var writer = Writer(stdout, "json");
        var selections = new[]
        {
            new OutputWriter.IndexSelectionLine("Auto/users/ByName", "chosen", "not the most up to date"),
            new OutputWriter.IndexSelectionLine("Auto/users/ByAge", "refused", null),
        };

        writer.OptimizerSelections("indexes", selections);
        var envelopes = await MachineEnvelope.AssertStreamWellFormedAsync(stdout.ToString(), "indexes", "host");
        await Assert.That(envelopes.Count).IsEqualTo(1);

        var payload = envelopes[0].GetProperty("result");
        var rows = payload.GetProperty("selections");
        await Assert.That(rows.GetArrayLength()).IsEqualTo(2);
        await Assert.That(rows[0].GetProperty("index").GetString()).IsEqualTo("Auto/users/ByName");
        await Assert.That(rows[0].GetProperty("reason").GetString()).IsEqualTo("not the most up to date");
        // A reason the host did not report is absent from the payload, not
        // written as null: absence is the honest form.
        await Assert.That(rows[1].TryGetProperty("reason", out _)).IsFalse();
        await Assert.That(payload.GetProperty("count").GetInt32()).IsEqualTo(2);
        await Assert.That(payload.GetProperty("withoutReason").GetInt32()).IsEqualTo(1);
    }
}
