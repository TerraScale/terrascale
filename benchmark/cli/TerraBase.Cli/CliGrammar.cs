// <copyright file="CliGrammar.cs" company="TerraScale">
// TerraBaseV2 (SE-13). See LICENSE.md.
// </copyright>

namespace TerraBase.Cli;

/// <summary>
/// The command grammar of the CLI: a fixed table of multi-word
/// commands, each with its operand count and its allowed options.
/// Parsing fails closed — an unknown option, an unknown command, a
/// missing operand, or an option outside its command is a usage error
/// that runs nothing.
/// </summary>
public static class CliGrammar
{
    private static readonly Dictionary<string, (int Operands, string[] Flags)> Commands = new(StringComparer.Ordinal)
    {
        ["help"] = (0, []),
        ["status"] = (0, []),
        ["document read"] = (2, []),
        ["document write"] = (3, ["idempotency-key", "expected"]),
        ["document delete"] = (2, ["idempotency-key", "expected"]),
        ["document browse"] = (1, ["max-items"]),
        ["query"] = (1, ["max-items"]),
        ["query cancel"] = (1, []),
        ["changes"] = (0, ["follow", "from", "collections", "idle", "max-events"]),
        ["sessions"] = (0, []),
        ["audit"] = (0, ["limit"]),
        ["metrics"] = (0, []),
        ["engine health"] = (0, []),
        ["engine status"] = (0, []),
        ["engine topology"] = (0, []),
        ["engine shutdown"] = (0, ["reason"]),
        ["schema status"] = (1, []),
        ["schema validate"] = (2, []),
        ["migration dry-run"] = (2, []),
        ["migration apply"] = (2, ["idempotency-key", "acknowledge-not-rollbackable"]),
    };

    private static readonly HashSet<string> BooleanFlags =
        ["json", "follow", "acknowledge-not-rollbackable"];

    private static readonly HashSet<string> GlobalFlags =
        ["mode", "url", "socket", "principal", "tenant", "output", "json"];

    /// <summary>One parsed invocation: the command and its operands and options.</summary>
    /// <param name="Name">The command's table name.</param>
    /// <param name="DisplayName">The command as the machine envelope names it.</param>
    /// <param name="Operands">The positional operands, in order.</param>
    /// <param name="Options">The command options by flag name (long form, no dashes).</param>
    public sealed record Command(string Name, string DisplayName, IReadOnlyList<string> Operands, IReadOnlyDictionary<string, string> Options)
    {
        /// <summary>Reads one option's value, or the default.</summary>
        /// <param name="name">The flag name.</param>
        /// <param name="fallback">The default.</param>
        /// <returns>The value.</returns>
        public string Option(string name, string fallback) =>
            this.Options.TryGetValue(name, out var value) ? value : fallback;
    }

    /// <summary>Tokenizes the arguments in one walk: the flags (long form, <c>--name value</c> or <c>--name=value</c>) and the plain tokens (the command words and operands) — an option's value is never mistaken for a command word.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>The flags by long name (boolean flags map to an empty value) and the plain tokens in order.</returns>
    public static (Dictionary<string, string> Flags, List<string> Plain) Tokenize(string[] args)
    {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        var plain = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                plain.Add(token);
                continue;
            }

            var body = token[2..];
            string name;
            string? inline = null;
            var equals = body.IndexOf('=');
            if (equals >= 0)
            {
                name = body[..equals];
                inline = body[(equals + 1)..];
            }
            else
            {
                name = body;
            }

            if (!GlobalFlags.Contains(name) && !Commands.Values.SelectMany(command => command.Flags).Contains(name))
            {
                throw new SurfaceUsageException($"unknown option --{name}");
            }

            if (BooleanFlags.Contains(name))
            {
                flags[name] = string.Empty;
                continue;
            }

            string? value = inline;
            if (value is null)
            {
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new SurfaceUsageException($"the option --{name} takes a value");
                }

                value = args[++index];
            }

            if (value.Length == 0)
            {
                throw new SurfaceUsageException($"the option --{name} takes a non-empty value");
            }

            flags[name] = value;
        }

        return (flags, plain);
    }

    /// <summary>Parses the flags of the arguments (long form, <c>--name value</c> or <c>--name=value</c>).</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>The flags by long name; boolean flags map to an empty value.</returns>
    public static Dictionary<string, string> ParseFlags(string[] args) => Tokenize(args).Flags;

    /// <summary>
    /// Names the command the plain tokens address: the longest matching
    /// table prefix — the same longest-prefix walk <see cref="ParseCommand"/>
    /// uses — so every multi-word command (<c>document read</c>,
    /// <c>query cancel</c>, <c>engine shutdown</c>, ...) is named by its
    /// full path, never by its first word alone.
    /// </summary>
    /// <param name="plain">The plain tokens, in order (an option's value never among them).</param>
    /// <returns>The matched table name, or the first token when nothing matches; the empty string for no tokens.</returns>
    public static string CommandLabel(IReadOnlyList<string> plain)
    {
        if (plain.Count == 0)
        {
            return string.Empty;
        }

        for (var words = Math.Min(2, plain.Count); words >= 1; words--)
        {
            var candidate = string.Join(' ', plain.Take(words));
            if (Commands.ContainsKey(candidate))
            {
                return candidate;
            }
        }

        return plain[0];
    }

    /// <summary>Parses the command: the longest matching table prefix of the plain tokens.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>The parsed command.</returns>
    public static Command ParseCommand(string[] args)
    {
        var (flags, plain) = Tokenize(args);
        if (plain.Count == 0)
        {
            throw new SurfaceUsageException("a command is required");
        }

        // The longest prefix wins, so "query cancel 1" is the cancel
        // command with operand 1, never "query" with the text "cancel".
        for (var words = Math.Min(2, plain.Count); words >= 1; words--)
        {
            var candidate = string.Join(' ', plain.Take(words));
            if (Commands.TryGetValue(candidate, out var shape))
            {
                List<string> operands = [.. plain.Skip(words)];
                if (operands.Count != shape.Operands)
                {
                    throw new SurfaceUsageException(
                        $"{candidate} takes {shape.Operands} operand{(shape.Operands == 1 ? string.Empty : "s")}, {operands.Count} given");
                }

                foreach (var flag in flags)
                {
                    if (!GlobalFlags.Contains(flag.Key) && !shape.Flags.Contains(flag.Key))
                    {
                        throw new SurfaceUsageException($"--{flag.Key} is not a {candidate} option");
                    }
                }

                return new Command(candidate, candidate, operands, flags);
            }
        }

        throw new SurfaceUsageException($"unknown command \"{string.Join(' ', plain.Take(2))}\"");
    }
}
