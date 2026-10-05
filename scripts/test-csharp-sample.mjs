import { mkdirSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { execFile } from 'node:child_process'
import { promisify } from 'node:util'

const execute = promisify(execFile)

export async function testCsharpSample({ code, directory, origin, project, fullKey, writeKey, certificate }) {
  const workspace = path.join(directory, 'csharp')
  mkdirSync(workspace)
  writeFileSync(path.join(workspace, 'Sample.csproj'), `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
  </PropertyGroup>
</Project>`)
  writeFileSync(path.join(workspace, 'Program.cs'), `${code}

static class Program
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static async Task Expect<T>(Func<Task> action, Func<T, bool> matches) where T : Exception
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (T error) when (matches(error)) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    static async Task Main()
    {
        var origin = new Uri(Environment.GetEnvironmentVariable("SAMPLE_ORIGIN")!);
        var project = Environment.GetEnvironmentVariable("SAMPLE_PROJECT")!;
        var fullKey = Environment.GetEnvironmentVariable("SAMPLE_FULL_KEY")!;
        var writeKey = Environment.GetEnvironmentVariable("SAMPLE_WRITE_KEY")!;
        Environment.SetEnvironmentVariable("TERRABASE_API_KEY", fullKey);
        using var setup = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        setup.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fullKey);
        foreach (var collection in new[] { "csharp_success", "csharp_create401", "csharp_query403" })
        {
            using var response = await setup.PostAsJsonAsync(new Uri(origin,
                "/v1/projects/" + Uri.EscapeDataString(project) + "/databases/samples/collections"),
                new { name = collection });
            response.EnsureSuccessStatusCode();
        }
        Task<string> Run(string collection, Uri? target = null, CancellationToken token = default) =>
            DocumentsExample.CreateAndQueryCustomers(_ => throw new Exception("Override must skip discovery"),
                new[] { origin }, project, "samples", collection, target ?? origin, token);

        var result = await Run("csharp_success");
        using (var json = System.Text.Json.JsonDocument.Parse(result))
            Check(json.RootElement.GetProperty("items").EnumerateArray().Any(item =>
                item.GetProperty("document").GetProperty("email").GetString() == "team@example.com" &&
                item.GetProperty("document").GetProperty("plan").GetString() == "pro"), "Missing returned customer");
        Console.WriteLine("csharp success: returned email/plan passed");

        Environment.SetEnvironmentVariable("TERRABASE_API_KEY", "tb_invalid");
        await Expect<HttpRequestException>(() => Run("csharp_create401"), error => error.StatusCode == System.Net.HttpStatusCode.Unauthorized);
        Console.WriteLine("csharp create401: actual TerraBase rejection passed");
        Environment.SetEnvironmentVariable("TERRABASE_API_KEY", writeKey);
        await Expect<HttpRequestException>(() => Run("csharp_query403"), error => error.StatusCode == System.Net.HttpStatusCode.Forbidden);
        Console.WriteLine("csharp query403: actual TerraBase rejection passed");
        Environment.SetEnvironmentVariable("TERRABASE_API_KEY", " ");
        await Expect<InvalidOperationException>(() => Run("csharp_success"), error => error.Message.Contains("TERRABASE_API_KEY"));
        Console.WriteLine("csharp blank key: validation passed");
        Environment.SetEnvironmentVariable("TERRABASE_API_KEY", fullKey);
        await Expect<ArgumentException>(() => Run("csharp_success", new Uri("https://untrusted.invalid")), error => error.Message.Contains("trusted HTTPS origin"));
        Console.WriteLine("csharp untrusted origin: validation passed");

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Expect<OperationCanceledException>(() => Run("csharp_success", token: cancelled.Token), error => error.CancellationToken.IsCancellationRequested);
        }
        Console.WriteLine("csharp caller cancellation: passed");
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            var unresolved = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken resolverToken = default;
            await Expect<OperationCanceledException>(() => DocumentsExample.CreateAndQueryCustomers(token => {
                resolverToken = token;
                return unresolved.Task;
            }, new[] { origin }, project, "samples", "csharp_success", cancellationToken: cancellation.Token),
                error => error.CancellationToken.IsCancellationRequested);
            Check(resolverToken.IsCancellationRequested, "Resolver did not receive linked cancellation");
            Check(!unresolved.Task.IsCompleted, "Resolver should remain unsettled");
        }
        Console.WriteLine("csharp unsettled resolver: caller cancellation terminates wait passed");
    }
}
`)
  const env = { ...process.env, SSL_CERT_FILE: certificate, SAMPLE_ORIGIN: origin,
    SAMPLE_PROJECT: typeof project === 'string' ? project : project.id,
    SAMPLE_FULL_KEY: fullKey, SAMPLE_WRITE_KEY: writeKey, DOTNET_CLI_TELEMETRY_OPTOUT: '1' }
  await execute('dotnet', ['build', 'Sample.csproj', '--configuration', 'Release', '--nologo'],
    { cwd: workspace, env, encoding: 'utf8', timeout: 60_000, killSignal: 'SIGKILL' })
  const run = await execute('dotnet', ['bin/Release/net10.0/Sample.dll'],
    { cwd: workspace, env, encoding: 'utf8', timeout: 45_000, killSignal: 'SIGKILL' })
  process.stdout.write(run.stdout)
}
