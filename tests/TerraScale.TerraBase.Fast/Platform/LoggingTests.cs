using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class LoggingTests
{
    [Test]
    public async Task Console_records_use_the_terrascale_record_v1_formatter()
    {
        await using var server = await TerraBaseTestServer.StartAsync();

        var console = server.Services.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value;
        await Assert.That(console.FormatterName).IsEqualTo("terrascale");
    }
}
