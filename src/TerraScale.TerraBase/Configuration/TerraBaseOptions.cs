using System.Collections.Frozen;
using System.Globalization;

namespace TerraScale.TerraBase.Configuration;

/// <summary>An invalid or incomplete <c>TERRABASE_*</c> configuration. The process must not start.</summary>
public sealed class TerraBaseConfigurationException(string message) : Exception(message);

/// <summary>
/// Validated process configuration.
/// Every setting is <c>TERRABASE_&lt;NAME&gt;</c> or the equivalent <c>--name=value</c> argument;
/// an argument wins over the environment.
/// </summary>
public sealed record TerraBaseOptions
{
    /// <summary>Default port of the loopback administrative and metrics listener.</summary>
    public const int DefaultAdminPort = 9464;

    private const string EnvironmentPrefix = "TERRABASE_";

    private static readonly FrozenSet<string> Keys = FrozenSet.Create(StringComparer.Ordinal, "admin-port");

    /// <summary>Port of the loopback administrative listener; <c>0</c> picks a free port.</summary>
    public required int AdminPort { get; init; }

    /// <summary>Reads and validates the configuration of this process.</summary>
    public static TerraBaseOptions FromProcess(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in Keys)
        {
            var value = Environment.GetEnvironmentVariable(EnvironmentPrefix + key.Replace('-', '_').ToUpperInvariant());
            if (!string.IsNullOrEmpty(value))
            {
                values[key] = value;
            }
        }

        foreach (var arg in args)
        {
            var separator = arg.IndexOf('=', StringComparison.Ordinal);
            if (!arg.StartsWith("--", StringComparison.Ordinal) || separator < 3)
            {
                throw new TerraBaseConfigurationException($"unrecognized argument '{arg.Split('=')[0]}'; use --name=value");
            }

            values[arg[2..separator]] = arg[(separator + 1)..];
        }

        return FromValues(values);
    }

    /// <summary>Validates settings keyed by their argument name.</summary>
    public static TerraBaseOptions FromValues(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var key in values.Keys)
        {
            if (!Keys.Contains(key))
            {
                throw new TerraBaseConfigurationException($"unknown setting '{key}'");
            }
        }

        var adminPort = DefaultAdminPort;
        if (values.TryGetValue("admin-port", out var rawPort)
            && (!int.TryParse(rawPort, NumberStyles.None, CultureInfo.InvariantCulture, out adminPort) || adminPort > ushort.MaxValue))
        {
            throw new TerraBaseConfigurationException("admin-port must be an integer between 0 and 65535");
        }

        return new TerraBaseOptions { AdminPort = adminPort };
    }
}
