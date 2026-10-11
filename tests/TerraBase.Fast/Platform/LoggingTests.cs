using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraBase.Hosting;
using TerraScale.Logging;
using ZLogger;
using ZLogger.Providers;

namespace TerraBase.Fast.Platform;

public sealed class LoggingTests
{
    [Test]
    public async Task Console_records_are_record_v1_json_with_fields_in_order()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.AddTerraBaseLogging();
        // The sink reads the same record v1 formatter as stdout, so the test never redirects the process-wide console.
        var captured = new List<string>();
        builder.Logging.AddZLoggerInMemory(null, options => options.UseFormatter(() => new TerraScaleLogFormatter("terrabase")), processor => processor.MessageReceived += captured.Add);
        await using var app = builder.Build();

        await Assert.That(app.Services.GetServices<ILoggerProvider>().Any(provider => provider is ZLoggerLogProcessorLoggerProvider)).IsTrue();

        var count = 3;
        app.Services.GetRequiredService<ILogger<LoggingTests>>().ZLogInformation($"Wrote {count:@Count} records");

        // Other categories may log during startup; only this test's category is checked. The JSON starts at the first brace, past any journald priority prefix.
        var category = typeof(LoggingTests).FullName;
        var records = captured
            .Select(line => JsonNode.Parse(line[line.IndexOf('{')..])!.AsObject())
            .Where(parsed => parsed["component"]?.GetValue<string>() == category)
            .ToList();
        await Assert.That(records.Count).IsEqualTo(1).Because($"expected one record from {category}; captured: {string.Join(" | ", captured)}");

        var record = records[0];
        // trace_id and span_id appear only when an Activity is ambient, so they are left out of the order check.
        var keys = record.Select(property => property.Key).Where(name => name is not ("trace_id" or "span_id"));
        await Assert.That(string.Join(",", keys)).IsEqualTo("time,level,msg,service,component,Count");
        await Assert.That(record["msg"]!.GetValue<string>()).IsEqualTo("Wrote {Count} records");
        var service = Environment.GetEnvironmentVariable("TS_SERVICE") is { Length: > 0 } configured ? configured : "terrabase";
        await Assert.That(record["service"]!.GetValue<string>()).IsEqualTo(service);
        await Assert.That(record["Count"]!.GetValue<int>()).IsEqualTo(3);
    }
}
