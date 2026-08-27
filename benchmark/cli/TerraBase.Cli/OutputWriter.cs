// <copyright file="OutputWriter.cs" company="TerraScale">
// TerraBaseV2 (SE-13). See LICENSE.md.
// </copyright>

namespace TerraBase.Cli;

using System.Text;
using System.Text.Json;

/// <summary>
/// The output conventions of the CLI. Machine output is one stable,
/// versioned JSON object per line — <c>cliVersion</c>, <c>command</c>,
/// <c>mode</c>, then exactly one payload property (<c>result</c>,
/// <c>event</c>, or <c>notice</c>) — on standard output, with error
/// envelopes on standard error. Human output uses the glossary
/// vocabulary exactly, and a typed error is one bounded human line,
/// never a raw JSON envelope. Neither output ever carries a
/// credential: none exists on this local surface, and the
/// backend-supplied context is never echoed.
/// </summary>
public sealed class OutputWriter
{
    private readonly CliConfiguration configuration;
    private readonly TextWriter stdout;
    private readonly TextWriter stderr;
    private readonly JsonWriterOptions options = new() { Indented = false };

    /// <summary>Initializes the writer over the invocation's streams.</summary>
    /// <param name="configuration">The resolved configuration.</param>
    /// <param name="stdout">The standard output stream.</param>
    /// <param name="stderr">The standard error stream.</param>
    public OutputWriter(CliConfiguration configuration, TextWriter stdout, TextWriter stderr)
    {
        this.configuration = configuration;
        this.stdout = stdout;
        this.stderr = stderr;
    }

    /// <summary>Whether the machine convention is selected.</summary>
    public bool Machine => this.configuration.OutputFormat == "json";

    /// <summary>Writes one machine envelope with a free-form JSON payload.</summary>
    /// <param name="command">The command's display name.</param>
    /// <param name="payloadProperty">The payload property: result, event, or notice.</param>
    /// <param name="write">The payload writer.</param>
    public void Envelope(string command, string payloadProperty, Action<Utf8JsonWriter> write)
    {
        if (!this.Machine)
        {
            return;
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, this.options))
        {
            writer.WriteStartObject();
            writer.WriteNumber("cliVersion", Cli.MachineOutputVersion);
            writer.WriteString("command", command);
            writer.WriteString("mode", this.configuration.Mode);
            writer.WritePropertyName(payloadProperty);
            write(writer);
            writer.WriteEndObject();
        }

        this.stdout.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
        this.stdout.Flush();
    }

    /// <summary>Writes one machine envelope whose payload is a prepared element.</summary>
    /// <param name="command">The command's display name.</param>
    /// <param name="payloadProperty">The payload property.</param>
    /// <param name="payload">The payload element.</param>
    public void Envelope(string command, string payloadProperty, JsonElement payload) =>
        this.Envelope(command, payloadProperty, writer => payload.WriteTo(writer));

    /// <summary>
    /// Writes the typed error on standard error: the error envelope in the
    /// machine convention, one bounded human line in the human convention —
    /// never a raw JSON envelope in a human terminal.
    /// </summary>
    /// <param name="command">The command's display name.</param>
    /// <param name="code">The closed error code.</param>
    /// <param name="detail">The bounded detail.</param>
    public void Error(string command, string code, string detail)
    {
        if (!this.Machine)
        {
            this.HumanError($"{command} {code}: {detail}");
            return;
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, this.options))
        {
            writer.WriteStartObject();
            writer.WriteNumber("cliVersion", Cli.MachineOutputVersion);
            writer.WriteString("command", command);
            writer.WriteString("mode", this.configuration.Mode);
            writer.WriteStartObject("error");
            writer.WriteString("code", code);
            writer.WriteString("detail", detail);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        this.stderr.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
        this.stderr.Flush();
    }

    /// <summary>Writes one bounded human error line on standard error (the human convention's shape of a typed error).</summary>
    /// <param name="detail">The bounded error detail.</param>
    public void HumanError(string detail)
    {
        if (this.Machine)
        {
            return;
        }

        this.stderr.WriteLine($"terrabase: {detail}");
        this.stderr.Flush();
    }

    /// <summary>Writes one human line (machine mode writes nothing).</summary>
    /// <param name="line">The line.</param>
    public void Human(string line)
    {
        if (this.Machine)
        {
            return;
        }

        this.stdout.WriteLine(line);
        this.stdout.Flush();
    }

    /// <summary>Writes the help text.</summary>
    /// <param name="lines">The help lines.</param>
    public void Help(IReadOnlyList<string> lines)
    {
        if (this.Machine)
        {
            this.Envelope("help", "result", writer =>
            {
                // The payload value of the help envelope is the array of
                // lines: the envelope has already written the payload's
                // property name, so the value starts here.
                writer.WriteStartArray();
                foreach (var line in lines)
                {
                    writer.WriteStringValue(line);
                }

                writer.WriteEndArray();
            });
            return;
        }

        foreach (var line in lines)
        {
            this.stdout.WriteLine(line);
        }

        this.stdout.Flush();
    }
}
