using TerraScale.DotNet;

using var httpClient = new HttpClient();
var client = new TerraScaleClient(
    httpClient,
    new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.terrascale.tech"),
        ProjectId = "01J00000000000000000000000",
        ApiKey = "consumer-provided-at-runtime"
    });

Console.WriteLine(client.Repositories.GetType().FullName);
