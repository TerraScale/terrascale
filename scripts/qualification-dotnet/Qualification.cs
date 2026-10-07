using System.Text.Json;
using TerraScale.DotNet.Testing;
using TerraScale.DotNet.TerraBase;

public static class Qualification
{
    public static async Task<int> Main()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        // Missing configuration must fail before any request, without credential output.
        foreach (var name in new[] { "TERRASCALE_API_URL", "TERRASCALE_ACCESS_TOKEN", "TERRASCALE_PROJECT_ID",
            "TERRASCALE_DATABASE_NAME", "TERRASCALE_COLLECTION_NAME" })
            Environment.SetEnvironmentVariable(name, null);
        if (await Program.Main() != 1) throw new InvalidOperationException("Missing configuration did not fail.");

        var (server, _) = await TerraBaseTestServer.StartAsync(cancellationToken: deadline.Token);
        if (server is null) throw new InvalidOperationException("Real binary unavailable; qualification not passed.");
        await using var ownedServer = server;
        var tenant = await server.CreateTenantAsync(deadline.Token);
        var database = server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "quickstart");
        await database.CreateAsync(deadline.Token);
        await database.CreateCollectionAsync("posts", deadline.Token);
        await Program.RunAsync(server.Http, server.ClientAddress, tenant.ApiKey,
            tenant.Project.Id, "quickstart", "posts", deadline.Token);

        var readOnly = await server.CreateApiKeyAsync(tenant.Organization.Id,
            ["documents:read"], tenant.Project.Id, deadline.Token);
        try
        {
            await server.Documents(readOnly).Database(tenant.Project.Id, "quickstart")
                .CreateDocumentAsync("posts", JsonSerializer.SerializeToElement(new { forbidden = true }), deadline.Token);
            throw new InvalidOperationException("Read-only credential was allowed to write.");
        }
        catch (TerraBaseForbiddenException) { }
        try
        {
            await server.Documents("tb_" + new string('0', 64)).Database(tenant.Project.Id, "quickstart")
                .ListDocumentsAsync("posts", cancellationToken: deadline.Token);
            throw new InvalidOperationException("Invalid credential was accepted.");
        }
        catch (TerraBaseAuthenticationException) { }
        var remaining = await database.ListDocumentsAsync("posts", cancellationToken: deadline.Token);
        if (remaining.Items.Count != 0) throw new InvalidOperationException("Starter left a live document behind.");
        Console.WriteLine("PASS_REAL_TERRABASE_STARTER");
        return 0;
    }
}
