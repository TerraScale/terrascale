// <copyright file="MachineEnvelope.cs" company="TerraScale">
// TerraBaseV2 (SE-13). See LICENSE.md.
// </copyright>

namespace TerraBase.Cli.Tests;

using System.Text.Json;

/// <summary>
/// The machine-envelope contract helper of the SE-13 rows: every
/// machine line — a <c>result</c>, an <c>event</c>, a <c>notice</c>, or
/// an <c>error</c> envelope — carries the same stable header
/// (<c>cliVersion</c>, <c>command</c>, <c>mode</c>) and exactly one
/// payload property, so the shape is asserted once per representative
/// envelope instead of being re-derived row by row.
/// </summary>
internal static class MachineEnvelope
{
    private static readonly string[] PayloadProperties = ["result", "event", "notice", "error"];

    /// <summary>Parses the machine lines of one stream (one JSON object per line).</summary>
    /// <param name="text">The stream text.</param>
    /// <returns>The parsed envelopes, in order.</returns>
    public static List<JsonElement> Parse(string text) =>
        [.. text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0 && line[0] == '{')
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())];

    /// <summary>Asserts the stable header and the single payload property of one envelope.</summary>
    /// <param name="envelope">The parsed envelope.</param>
    /// <param name="command">The command the envelope must name.</param>
    /// <param name="mode">The mode the envelope must name.</param>
    /// <returns>A task that completes when the shape held.</returns>
    public static async Task AssertWellFormedAsync(JsonElement envelope, string command, string mode)
    {
        await Assert.That(envelope.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(envelope.GetProperty("cliVersion").GetInt32()).IsEqualTo(Cli.MachineOutputVersion);
        await Assert.That(envelope.GetProperty("command").GetString()).IsEqualTo(command);
        await Assert.That(envelope.GetProperty("mode").GetString()).IsEqualTo(mode);
        var carried = envelope.EnumerateObject().Count(property => PayloadProperties.Contains(property.Name, StringComparer.Ordinal));
        await Assert.That(carried).IsEqualTo(1);
    }

    /// <summary>Asserts the shape of every envelope of one stream.</summary>
    /// <param name="text">The stream text.</param>
    /// <param name="command">The command every envelope must name.</param>
    /// <param name="mode">The mode every envelope must name.</param>
    /// <returns>A task that completes when every shape held, with the envelopes as its result.</returns>
    public static async Task<List<JsonElement>> AssertStreamWellFormedAsync(string text, string command, string mode)
    {
        var envelopes = Parse(text);
        foreach (var envelope in envelopes)
        {
            await AssertWellFormedAsync(envelope, command, mode);
        }

        return envelopes;
    }
}
