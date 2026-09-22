namespace TerraScale.Dashboard.Infrastructure.Configuration;

/// <summary>
/// Loads a local <c>.env</c> / <c>.env.local</c> file into configuration during
/// development, so the Stripe sandbox secrets live outside source control. Real
/// process environment variables always win over the file, and the loader is a
/// no-op outside Development and for the <c>DASHBOARD_E2E=1</c> test host, which
/// must stay hermetic instead of inheriting a developer's sandbox keys.
/// </summary>
public static class DotEnvFile
{
    public static void Load(ConfigurationManager configuration, IHostEnvironment environment)
    {
        if (!environment.IsDevelopment() || configuration["DASHBOARD_E2E"] == "1")
        {
            return;
        }

        if (FindFile(environment.ContentRootPath) is not { } path)
        {
            return;
        }

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..].Trim();
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = Unquote(line[(separator + 1)..].Trim());
            if (key.Length == 0 || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
            {
                continue;
            }

            // .env files use the deployed environment-variable spelling
            // (Payments__Stripe__SecretKey); configuration reads nested keys.
            values[key.Replace("__", ":", StringComparison.Ordinal)] = value;
        }

        if (values.Count > 0)
        {
            configuration.AddInMemoryCollection(values);
        }
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;

    /// <summary>Walks up from the content root so a repo-root file is found
    /// when the app runs from its project directory.</summary>
    private static string? FindFile(string contentRoot)
    {
        var directory = new DirectoryInfo(contentRoot);
        for (var depth = 0; directory is not null && depth < 4; depth++, directory = directory.Parent)
        {
            foreach (var name in new[] { ".env.local", ".env" })
            {
                var candidate = Path.Combine(directory.FullName, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
