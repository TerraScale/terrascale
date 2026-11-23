using System.Collections;
using System.Globalization;

namespace TerraBase.Configuration;

public sealed record ConfigurationError(string Message);

internal sealed class SettingValues(Dictionary<string, string> values)
{
    private const string EnvironmentPrefix = "TERRABASE_";

    private readonly List<string> _errors = [];

    public IReadOnlyList<string> Errors => _errors;

    public bool Has(string key) => values.ContainsKey(key);

    public string? Get(string key) => values.GetValueOrDefault(key);

    public void Fail(string message) => _errors.Add(message);

    public static ConfigurationError? ReadSecretFiles(IEnumerable<(string Variable, string Key)> extraVariables)
    {
        var allowed = extraVariables.Select(static pair => pair.Variable).ToHashSet(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key || !key.EndsWith("_FILE", StringComparison.Ordinal) || entry.Value is not string { Length: > 0 } path)
            {
                continue;
            }

            var name = key[..^"_FILE".Length];
            if (!name.StartsWith(EnvironmentPrefix, StringComparison.Ordinal) && !allowed.Contains(name))
            {
                continue;
            }

            if (FileText.Read(path) is not { } text)
            {
                return new ConfigurationError(key + " must name a readable file");
            }

            Environment.SetEnvironmentVariable(name, text.TrimEnd('\r', '\n'));
        }

        return null;
    }

    public static SettingValues FromProcess(IReadOnlyCollection<string> keys, IReadOnlyList<(string Variable, string Key)> aliases, IReadOnlyList<string> args)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var value = Environment.GetEnvironmentVariable(EnvironmentPrefix + key.Replace('-', '_').ToUpperInvariant());
            if (!string.IsNullOrEmpty(value))
            {
                values[key] = value;
            }
        }

        if (aliases.All(static alias => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(alias.Variable))))
        {
            foreach (var (variable, key) in aliases)
            {
                if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value)
                {
                    values[key] = value;
                }
            }
        }

        var settings = new SettingValues(values);
        foreach (var arg in args)
        {
            var separator = arg.IndexOf('=', StringComparison.Ordinal);
            if (!arg.StartsWith("--", StringComparison.Ordinal) || separator < 3)
            {
                settings.Fail(string.Concat("unrecognized argument '", arg.Split('=')[0], "'; use --name=value"));
                continue;
            }

            var key = arg[2..separator];
            var value = arg[(separator + 1)..];
            if (key is "environment" or "applicationName" or "contentRoot")
            {
                continue;
            }

            if (!keys.Contains(key))
            {
                settings.Fail(string.Concat("unknown setting '", key, "'"));
                continue;
            }

            if (value.Length == 0)
            {
                values.Remove(key);
            }
            else
            {
                values[key] = value;
            }
        }

        return settings;
    }

    public int Port(string key, int fallback)
    {
        if (Get(key) is not { } raw)
        {
            return fallback;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port > ushort.MaxValue)
        {
            Fail(key + " must be an integer between 0 and 65535");
            return fallback;
        }

        return port;
    }

    public long Integer(string key, long min, long max, long fallback)
    {
        if (Get(key) is not { } raw)
        {
            return fallback;
        }

        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
        {
            Fail(string.Create(CultureInfo.InvariantCulture, $"{key} must be an integer between {min} and {max}"));
            return fallback;
        }

        return value;
    }

    public string Token(string key)
    {
        if (Get(key) is { Length: > 0 and <= 512 } value && !value.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            return value;
        }

        Fail(key + " must be 1 to 512 characters without whitespace");
        return "";
    }
}

internal static class FileText
{
    public static string? Read(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
