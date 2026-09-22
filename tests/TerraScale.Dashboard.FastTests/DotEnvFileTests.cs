using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using TerraScale.Dashboard.Infrastructure.Configuration;

namespace TerraScale.Dashboard.FastTests;

/// <summary>The development .env loader: nested keys, quotes, comments, and the
/// production no-op.</summary>
public sealed class DotEnvFileTests
{
    private static async Task<string> WriteEnvFileAsync(string contents)
    {
        var directory = Directory.CreateTempSubdirectory("ts-dotenv-");
        await File.WriteAllTextAsync(Path.Combine(directory.FullName, ".env.local"), contents);
        return directory.FullName;
    }

    [Test]
    public async Task LoadsNestedKeysAndQuotesInDevelopment()
    {
        var root = await WriteEnvFileAsync(
            "# sandbox settings\nPayments__Stripe__Mode=test\nPayments__Stripe__SecretKey=\"sk_test_from_file\"\n");
        try
        {
            var configuration = new ConfigurationManager();
            DotEnvFile.Load(configuration, new HostingEnvironment
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = root,
            });

            await Assert.That(configuration["Payments:Stripe:Mode"]).IsEqualTo("test");
            await Assert.That(configuration["Payments:Stripe:SecretKey"]).IsEqualTo("sk_test_from_file");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task IsANoOpOutsideDevelopment()
    {
        var root = await WriteEnvFileAsync("Payments__Stripe__Mode=test\n");
        try
        {
            var configuration = new ConfigurationManager();
            DotEnvFile.Load(configuration, new HostingEnvironment
            {
                EnvironmentName = Environments.Production,
                ContentRootPath = root,
            });

            await Assert.That(configuration["Payments:Stripe:Mode"]).IsNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task IsANoOpForTheHermeticTestHost()
    {
        var root = await WriteEnvFileAsync("Payments__Stripe__Mode=test\n");
        try
        {
            var configuration = new ConfigurationManager { ["DASHBOARD_E2E"] = "1" };
            DotEnvFile.Load(configuration, new HostingEnvironment
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = root,
            });

            await Assert.That(configuration["Payments:Stripe:Mode"]).IsNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
