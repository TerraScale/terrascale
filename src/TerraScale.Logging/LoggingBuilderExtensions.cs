using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZLogger;
using ZLogger.Providers;

namespace TerraScale.Logging;

/// <summary>TerraScale logging registration.</summary>
public static class LoggingBuilderExtensions
{
    /// <summary>
    /// Replaces the logging providers with a ZLogger processor that writes TerraScale record v1 lines to stdout
    /// and sets the TerraScale default filters in code. <paramref name="service"/> is used when <c>TS_SERVICE</c> is unset.
    /// <paramref name="levels"/> sets the minimum level for each category the service owns, such as a framework base class
    /// that logs under the service's own type.
    /// </summary>
    public static ILoggingBuilder AddTerraScaleLogging(this ILoggingBuilder builder, string service, params (string Category, LogLevel Level)[] levels) =>
        builder.AddTerraScaleLogging(service, null, Environment.GetEnvironmentVariable, TimeProvider.System, levels);

    // Tests and benchmarks pass their own writer, environment and clock through InternalsVisibleTo. Null means Console.Out.
    internal static ILoggingBuilder AddTerraScaleLogging(
        this ILoggingBuilder builder,
        string service,
        TextWriter? output,
        Func<string, string?> environment,
        TimeProvider timeProvider,
        params (string Category, LogLevel Level)[] levels)
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
        var processor = new TerraScaleLogProcessor(new TerraScaleLogFormatter(service, environment), output);
        builder.AddZLoggerLogProcessor(options =>
        {
            options.IncludeScopes = true;
            options.TimeProvider = timeProvider;
            // Keep standard log messages lazy: a throwing ToString() then becomes <unformattable> in the formatter.
            options.IsFormatLogImmediatelyInStandardLog = false;
            return processor;
        });
        // PostConfigure runs after every Configure, so a host's Logging section cannot override these values.
        builder.Services.PostConfigure<LoggerFilterOptions>(options =>
        {
            // Other providers register their own rules; only the rules that can reach stdout are replaced.
            foreach (var rule in options.Rules.Where(ReachesStdout).ToList())
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

    private static readonly string ProcessorTypeName = typeof(ZLoggerLogProcessorLoggerProvider).FullName!;
    private static readonly string? ProcessorAlias = typeof(ZLoggerLogProcessorLoggerProvider).GetCustomAttribute<ProviderAliasAttribute>()?.Alias;

    private static bool ReachesStdout(LoggerFilterRule rule) =>
        string.IsNullOrEmpty(rule.ProviderName) || rule.ProviderName == ProcessorTypeName || rule.ProviderName == ProcessorAlias;
}
