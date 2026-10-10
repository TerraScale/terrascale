using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace TerraScale.Logging;

/// <summary>TerraScale logging registration.</summary>
public static class LoggingBuilderExtensions
{
    /// <summary>
    /// Replaces the logging providers with a console logger that writes TerraScale record v1 lines
    /// and sets the TerraScale default filters in code. <paramref name="service"/> is used when <c>TS_SERVICE</c> is unset.
    /// <paramref name="levels"/> sets the minimum level for each category the service owns, such as a framework base class
    /// that logs under the service's own type.
    /// </summary>
    public static ILoggingBuilder AddTerraScaleLogging(this ILoggingBuilder builder, string service, params (string Category, LogLevel Level)[] levels)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentNullException.ThrowIfNull(levels);

        var categories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (category, _) in levels)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(category);
            if (!categories.Add(category))
            {
                throw new ArgumentException($"Logging category '{category}' is listed more than once.", nameof(levels));
            }
        }

        builder.ClearProviders();
        // Same registration AddConsoleFormatter performs, without its trim/AOT-annotated generic overload.
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ConsoleFormatter, TerraScaleConsoleFormatter>());
        builder.Services.Configure<TerraScaleFormatterOptions>(options =>
        {
            options.Service = service;
            options.IncludeScopes = true;
        });
        builder.AddConsole();
        // PostConfigure runs after every Configure, so a host's Logging section cannot override these values.
        builder.Services.PostConfigure<ConsoleLoggerOptions>(options => options.FormatterName = TerraScaleConsoleFormatter.FormatterName);
        builder.Services.PostConfigure<LoggerFilterOptions>(options =>
        {
            // Other providers register their own rules; only the rules that can reach the console are replaced.
            foreach (var rule in options.Rules.Where(ReachesConsole).ToList())
            {
                options.Rules.Remove(rule);
            }
            options.MinLevel = LogLevel.Information;
            options.Rules.Add(new LoggerFilterRule(providerName: null, categoryName: "Microsoft.AspNetCore", logLevel: LogLevel.Warning, filter: null));
            options.Rules.Add(new LoggerFilterRule(providerName: null, categoryName: "System.Net.Http.HttpClient", logLevel: LogLevel.Warning, filter: null));
            options.Rules.Add(new LoggerFilterRule(providerName: null, categoryName: "Microsoft.Hosting.Lifetime", logLevel: LogLevel.Information, filter: null));
            foreach (var (category, level) in levels)
            {
                options.Rules.Add(new LoggerFilterRule(providerName: null, categoryName: category, logLevel: level, filter: null));
            }
        });
        return builder;
    }

    private const string ConsoleAlias = "Console";
    private const string ConsoleTypeName = "Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider";

    private static bool ReachesConsole(LoggerFilterRule rule) =>
        string.IsNullOrEmpty(rule.ProviderName) || rule.ProviderName is ConsoleAlias or ConsoleTypeName;
}
