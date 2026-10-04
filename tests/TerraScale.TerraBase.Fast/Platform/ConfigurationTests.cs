using TerraScale.TerraBase.Configuration;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Platform;

public sealed class ConfigurationTests
{
    [Test]
    public async Task Unknown_settings_are_rejected_without_echoing_values()
    {
        var error = await Assert.That(() => TerraBaseOptions.FromProcess(["--admin-token=secret-value"]))
            .Throws<TerraBaseConfigurationException>();

        await Assert.That(error!.Message).Contains("admin-token");
        await Assert.That(error.Message).DoesNotContain("secret-value");
    }

    [Test]
    [Arguments("-1")]
    [Arguments("65536")]
    [Arguments("port")]
    public async Task Invalid_admin_ports_are_rejected(string port)
    {
        await Assert.That(() => TerraBaseOptions.FromProcess([$"--admin-port={port}"]))
            .Throws<TerraBaseConfigurationException>();
    }

    [Test]
    public async Task Invalid_data_directories_are_rejected_without_echoing_them()
    {
        var error = await Assert.That(() => TerraBaseOptions.FromProcess(["--data-directory=secret\0path"]))
            .Throws<TerraBaseConfigurationException>();

        await Assert.That(error!.Message).Contains("data-directory");
        await Assert.That(error.Message).DoesNotContain("secret");
    }

    [Test]
    public async Task An_empty_argument_clears_a_setting()
    {
        var options = TerraBaseOptions.FromProcess(["--peer-port=", "--admin-port=0"]);

        await Assert.That(options.PeerPort).IsNull();
        await Assert.That(options.AdminPort).IsEqualTo(0);
    }

    [Test]
    public async Task The_dashboard_credential_needs_the_client_listener_and_a_long_secret()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-credential-").FullName;
        try
        {
            var shortSecret = Path.Combine(directory, "short");
            await File.WriteAllTextAsync(shortSecret, "secret-value");
            var longSecret = Path.Combine(directory, "long");
            await File.WriteAllTextAsync(longSecret, new string('s', 32) + "\n");
            var blankSecret = Path.Combine(directory, "blank");
            await File.WriteAllTextAsync(blankSecret, new string(' ', 40));
            using var tls = new ClientTls();
            var client = new Dictionary<string, string>();
            tls.Apply(client);

            var withoutClient = await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string> { ["dashboard-credential-file"] = longSecret }))
                .Throws<TerraBaseConfigurationException>();
            var tooShort = await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string>(client) { ["dashboard-credential-file"] = shortSecret }))
                .Throws<TerraBaseConfigurationException>();
            var missing = await Assert.That(() => TerraBaseOptions.FromValues(
                    new Dictionary<string, string>(client) { ["dashboard-credential-file"] = Path.Combine(directory, "missing") }))
                .Throws<TerraBaseConfigurationException>();
            var blank = await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string>(client) { ["dashboard-credential-file"] = blankSecret }))
                .Throws<TerraBaseConfigurationException>();
            var options = TerraBaseOptions.FromValues(new Dictionary<string, string>(client) { ["dashboard-credential-file"] = longSecret });

            await Assert.That(withoutClient!.Message).Contains("client-port");
            await Assert.That(tooShort!.Message).Contains("dashboard-credential-file");
            await Assert.That(tooShort.Message).DoesNotContain("secret-value");
            await Assert.That(missing!.Message).DoesNotContain(directory);
            // A file of only whitespace is trimmed to an empty, too short secret.
            await Assert.That(blank!.Message).Contains("dashboard-credential-file");
            await Assert.That(blank.Message).DoesNotContain(directory);
            await Assert.That(options.DashboardCredentialHash.Length).IsEqualTo(32);
            foreach (var owner in new[] { "bad\u0001owner", "", "   ", new string('o', 257) })
            {
                await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string> { ["management-legacy-owner"] = owner }))
                    .Throws<TerraBaseConfigurationException>();
            }

            await Assert.That(() => TerraBaseOptions.FromProcess(["--management-legacy-owner=  "])).Throws<TerraBaseConfigurationException>();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
