using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZLogger;
using ZLogger.Providers;

namespace TerraScale.Logging;

public static class LoggingBuilderExtensions
{
    public static ILoggingBuilder AddTerraScaleLogging(this ILoggingBuilder builder, string service, params (string Category, LogLevel Level)[] levels) =>
        builder.AddTerraScaleLogging(service, null, Environment.GetEnvironmentVariable, TimeProvider.System, levels);

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
            options.IsFormatLogImmediatelyInStandardLog = false;
            return processor;
        });
        builder.Services.PostConfigure<LoggerFilterOptions>(options =>
        {
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

    private static readonly string ProcessorTypeName = typeof(ZLoggerLogProcessorLoggerProvider).FullName ?? nameof(ZLoggerLogProcessorLoggerProvider);
    private static readonly string? ProcessorAlias = typeof(ZLoggerLogProcessorLoggerProvider).GetCustomAttribute<ProviderAliasAttribute>()?.Alias;

    private static bool ReachesStdout(LoggerFilterRule rule) =>
        string.IsNullOrEmpty(rule.ProviderName) || rule.ProviderName == ProcessorTypeName || rule.ProviderName == ProcessorAlias;
}
