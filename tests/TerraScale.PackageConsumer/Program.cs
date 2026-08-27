using TerraScale.DotNet;

var baseAddress = new Uri(
    Environment.GetEnvironmentVariable("TERRASCALE_PACKAGE_CONSUMER_BASE_URL")
        ?? "https://api.terrascale.tech");
using var httpClient = new HttpClient();
var client = new TerraScaleClient(
    httpClient,
    new TerraScaleClientOptions
    {
        BaseAddress = baseAddress,
        ProjectId = Environment.GetEnvironmentVariable("TERRASCALE_PACKAGE_CONSUMER_PROJECT_ID"),
        ApiKey = Environment.GetEnvironmentVariable("TERRASCALE_PACKAGE_CONSUMER_API_KEY"),
        BearerToken = Environment.GetEnvironmentVariable("TERRASCALE_PACKAGE_CONSUMER_BEARER_TOKEN")
    });

Console.WriteLine(client.Repositories.GetType().FullName);

if (string.Equals(
        Environment.GetEnvironmentVariable("TERRASCALE_PACKAGE_CONSUMER_RUN_TYPED_OPERATION"),
        "true",
        StringComparison.OrdinalIgnoreCase))
{
    var databases = await client.Databases.ListAsync();
    Console.WriteLine($"Typed database operation returned {databases.Count} item(s).");
}
