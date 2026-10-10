using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace TerraScale.Logging.Tests;

public sealed class LoggingBuilderTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task HostConfigurationCannotOverrideCodeDefaults(bool configurationFirst)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Debug",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Debug",
            ["Logging:Console:LogLevel:Default"] = "Debug",
            ["Logging:Console:FormatterName"] = "simple",
        }).Build();
        using var provider = new ServiceCollection().AddLogging(builder =>
        {
            if (configurationFirst)
            {
                builder.AddConfiguration(configuration.GetSection("Logging"));
                builder.AddTerraScaleLogging("terrabase");
            }
            else
            {
                builder.AddTerraScaleLogging("terrabase");
                builder.AddConfiguration(configuration.GetSection("Logging"));
            }
        }).BuildServiceProvider();
        var factory = provider.GetRequiredService<ILoggerFactory>();

        await Assert.That(factory.CreateLogger("Acme.Widgets").IsEnabled(LogLevel.Debug)).IsFalse();
        await Assert.That(factory.CreateLogger("Microsoft.AspNetCore.Routing").IsEnabled(LogLevel.Information)).IsFalse();
        await Assert.That(provider.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.FormatterName).IsEqualTo("terrascale");
    }

    [Test]
    public async Task OtherProviderRulesSurvive()
    {
        const string sentry = "Sentry.Extensions.Logging.SentryLoggerProvider";
        using var provider = new ServiceCollection().AddLogging(builder =>
        {
            builder.Services.Configure<LoggerFilterOptions>(options => options.Rules.Add(new LoggerFilterRule(sentry, "Acme.Widgets", LogLevel.Debug, filter: null)));
            builder.AddTerraScaleLogging("terrabase");
        }).BuildServiceProvider();
        var rules = provider.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules;

        await Assert.That(rules.Any(rule => rule.ProviderName == sentry && rule.CategoryName == "Acme.Widgets" && rule.LogLevel == LogLevel.Debug)).IsTrue();
    }

    [Test]
    public async Task DefaultFiltersAreSetInCode()
    {
        using var factory = LoggerFactory.Create(builder => builder.AddTerraScaleLogging("terrabase"));

        await Assert.That(factory.CreateLogger("Acme.Widgets").IsEnabled(LogLevel.Debug)).IsFalse();
        await Assert.That(factory.CreateLogger("Acme.Widgets").IsEnabled(LogLevel.Information)).IsTrue();
        await Assert.That(factory.CreateLogger("Microsoft.AspNetCore.Routing").IsEnabled(LogLevel.Information)).IsFalse();
        await Assert.That(factory.CreateLogger("Microsoft.AspNetCore.Routing").IsEnabled(LogLevel.Warning)).IsTrue();
        await Assert.That(factory.CreateLogger("System.Net.Http.HttpClient").IsEnabled(LogLevel.Information)).IsFalse();
        await Assert.That(factory.CreateLogger("Microsoft.Hosting.Lifetime").IsEnabled(LogLevel.Information)).IsTrue();
    }

    [Test]
    public async Task ServiceCategoryLevelsAreSetInCode()
    {
        using var factory = LoggerFactory.Create(builder => builder.AddTerraScaleLogging("terrabase",
            ("TerraBase.Features.ApiKeys.ApiKeyAuthenticationHandler", LogLevel.Warning)));

        await Assert.That(factory.CreateLogger("TerraBase.Features.ApiKeys.ApiKeyAuthenticationHandler").IsEnabled(LogLevel.Information)).IsFalse();
        await Assert.That(factory.CreateLogger("TerraBase.Features.ApiKeys.ApiKeyAuthenticationHandler").IsEnabled(LogLevel.Warning)).IsTrue();
        await Assert.That(factory.CreateLogger("Acme.Widgets").IsEnabled(LogLevel.Information)).IsTrue();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task HostConfigurationCannotOverrideServiceCategoryLevels(bool configurationFirst)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:TerraBase.Features.ApiKeys.ApiKeyAuthenticationHandler"] = "Debug",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Debug",
        }).Build();
        using var provider = new ServiceCollection().AddLogging(builder =>
        {
            if (configurationFirst)
            {
                builder.AddConfiguration(configuration.GetSection("Logging"));
                builder.AddTerraScaleLogging("terrabase", ("TerraBase.Features.ApiKeys.ApiKeyAuthenticationHandler", LogLevel.Warning));
            }
            else
            {
                builder.AddTerraScaleLogging("terrabase", ("TerraBase.Features.ApiKeys.ApiKeyAuthenticationHandler", LogLevel.Warning));
                builder.AddConfiguration(configuration.GetSection("Logging"));
            }
        }).BuildServiceProvider();
        var factory = provider.GetRequiredService<ILoggerFactory>();

        await Assert.That(factory.CreateLogger("TerraBase.Features.ApiKeys.ApiKeyAuthenticationHandler").IsEnabled(LogLevel.Information)).IsFalse();
        await Assert.That(factory.CreateLogger("Microsoft.AspNetCore.Routing").IsEnabled(LogLevel.Information)).IsFalse();
    }

    [Test]
    public async Task ServiceCategoryLevelsMustBeNamedOnceAndNonEmpty()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => LoggerFactory.Create(builder => builder.AddTerraScaleLogging("terrabase",
            ("Acme.Widgets", LogLevel.Warning),
            ("Acme.Widgets", LogLevel.Error)))));
        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => LoggerFactory.Create(builder => builder.AddTerraScaleLogging("terrabase",
            (" ", LogLevel.Warning)))));
    }
}
