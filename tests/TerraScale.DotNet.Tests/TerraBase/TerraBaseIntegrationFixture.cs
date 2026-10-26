using System.Net;
using System.Text;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

internal static class TerraBaseIntegrationFixture
{
    internal const string ProjectId = "01JA0000000000000000000001";
    internal static string Read(string name) => ReadContract("fixtures/" + name);

    internal static string ReadContract(string name) => Encoding.UTF8.GetString(ReadContractBytes(name));

    internal static byte[] ReadContractBytes(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "contracts", "terrabase", name);
            if (File.Exists(path)) return File.ReadAllBytes(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Missing source-derived TerraBase fixture", name);
    }

    internal static TerraBaseDocumentsClient Client(HttpClient http, TimeSpan? timeout = null) => timeout is null
        ? new(http, new TerraBaseDocumentsClientOptions { Region = "sjc", AccessTokenProvider = _ => ValueTask.FromResult("project-user-token") })
        : new(new TerraBaseConnection(http, TerraBaseRegion.PublicOrigin, null, null, "credential", timeout.Value,
            TerraBaseConnection.DefaultMaxResponseBytes, _ => ValueTask.FromResult("project-user-token"), region: "sjc"));

    internal static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}
