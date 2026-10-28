using System.Diagnostics;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ApiBench;

[SupportedOSPlatform("linux")]
internal static class Program
{
    // Startup only constructs clients, so this placeholder is never sent anywhere.
    private const string StartupKey = "01ARZ3NDEKTSV4RRFFQ69G5FAV_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const UnixFileMode GroupOrOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private static readonly object Gate = new();
    private static CancellationTokenSource? running;

    private static async Task<int> Main(string[] args)
    {
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, Interrupt);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Interrupt);
        return args switch
        {
            ["startup"] => Startup(),
            ["run", "--spec", var spec, "--credentials", var credentials] => await RunAsync(spec, credentials),
            ["parity", "--credentials", var credentials, "--out", var output] => await ParityAsync(credentials, output),
            _ => Usage(),
        };
    }

    private static int Startup()
    {
        using var bench = new Bench(new Uri("https://api-bench.invalid"), new Uri("https://auth-bench.invalid/startup"), "startup", StartupKey, 0);
        using var process = Process.GetCurrentProcess();
        Emit(new Startup("startup", (DateTime.Now - process.StartTime).TotalMilliseconds), Wire.Default.Startup);
        return 0;
    }

    private static async Task<int> RunAsync(string specPath, string credentialsPath)
    {
        Spec spec;
        Credentials credentials;
        try
        {
            spec = JsonSerializer.Deserialize(File.ReadAllBytes(specPath), Wire.Default.Spec) ?? throw new InvalidDataException("empty spec");
            if ((File.GetUnixFileMode(credentialsPath) & GroupOrOther) != 0)
            {
                Console.Error.WriteLine("api-bench: credentials file must have no group or other permissions (chmod 600)");
                return 2;
            }

            credentials = JsonSerializer.Deserialize(File.ReadAllBytes(credentialsPath), Wire.Default.Credentials) ?? throw new InvalidDataException("empty credentials");
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException)
        {
            // Parser messages can echo input bytes, so only the exception type is reported.
            Console.Error.WriteLine($"api-bench: spec or credentials could not be read ({error.GetType().Name})");
            return 2;
        }

        Bench bench;
        try
        {
            bench = new Bench(new Uri(spec.Endpoint), new Uri(spec.AuthIssuer), credentials.Project, credentials.ApiKey, spec.SeedDocuments);
        }
        catch (FormatException)
        {
            Console.Error.WriteLine("api-bench: credentials api_key is not a <ULID>_<52 base32> project key");
            return 2;
        }

        using (bench)
        {
            return await SessionAsync(bench);
        }
    }

    /// <summary>
    /// Main's SIGINT/SIGTERM registrations outlive this call, so repeated signals stay caught while the owned database is
    /// deleted; the first one cancels the run.
    /// </summary>
    private static async Task<int> ParityAsync(string credentials, string output)
    {
        using var cut = new CancellationTokenSource();
        lock (Gate) running = cut;
        try
        {
            return await ParityRun.RunAsync(credentials, output, ParityPeers.Read, ParityRig.Live, Console.Error, cut.Token);
        }
        finally
        {
            lock (Gate) running = null;
        }
    }

    /// <summary>Setup, then one result per stdin step line, then cleanup on EOF.</summary>
    private static async Task<int> SessionAsync(Bench bench)
    {
        var code = 0;
        try
        {
            Emit(await bench.SetupAsync(CancellationToken.None), Wire.Default.Ready);
            string? line;
            while ((line = await Console.In.ReadLineAsync()) is not null)
            {
                var step = JsonSerializer.Deserialize(line, Wire.Default.Step) ?? throw new JsonException("empty step");
                var cut = new CancellationTokenSource();
                lock (Gate) running = cut;
                try
                {
                    Emit(await bench.StepAsync(step, cut.Token), Wire.Default.StepResult);
                }
                finally
                {
                    lock (Gate) running = null;
                }
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"api-bench: stopped ({error.GetType().Name}, status {Http.StatusOf(error)})");
            code = 1;
        }

        Emit(await bench.CleanupAsync(), Wire.Default.Done);
        return code;
    }

    /// <summary>Cuts the running step short. The process keeps serving steps until stdin closes.</summary>
    private static void Interrupt(PosixSignalContext context)
    {
        lock (Gate) Catch(context, running);
    }

    /// <summary>Every signal is caught, never default-terminating the process; only the running work is canceled.</summary>
    internal static void Catch(PosixSignalContext context, CancellationTokenSource? work)
    {
        context.Cancel = true;
        work?.Cancel();
    }

    private static void Emit<T>(T value, JsonTypeInfo<T> info) => Console.Out.WriteLine(JsonSerializer.Serialize(value, info));

    private static int Usage()
    {
        Console.Error.WriteLine("usage: api-bench startup | api-bench run --spec PATH --credentials PATH | api-bench parity --credentials PATH --out PATH");
        return 2;
    }
}
