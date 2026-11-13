using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Engine;
using TerraBase.Tenancy;

public sealed class StartupHook : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private static readonly List<IStorageTransaction> Held = [];

    public static void Initialize() => DiagnosticListener.AllListeners.Subscribe(new StartupHook());

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == "Microsoft.Extensions.Hosting")
        {
            listener.Subscribe(this);
        }
    }

    public void OnNext(KeyValuePair<string, object?> entry)
    {
        if (entry is { Key: "HostBuilt", Value: IHost host })
        {
            host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() =>
                _ = ServeAsync(host.Services, host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
        }
    }

    public void OnError(Exception error) => throw error;

    public void OnCompleted() => Held.Clear();

    private static async Task ServeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var path = Environment.GetEnvironmentVariable("ENGINE_CRASH_SOCKET")!;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            Console.WriteLine("engine crash hook ready");
            while (!cancellationToken.IsCancellationRequested)
            {
                using var socket = await listener.AcceptAsync(cancellationToken);
                using var stream = new NetworkStream(socket);
                using var reader = new StreamReader(stream);
                await using var writer = new StreamWriter(stream) { AutoFlush = true };
                var parts = (await reader.ReadLineAsync(cancellationToken))!.Split(' ');
                try
                {
                    var engine = services.GetRequiredService<IStorageEngine>();
                    var tenant = new TenantContext(parts[1], PlanTier.Hobby, parts[2], parts[3]);
                    if (parts[0] == "hold")
                    {
                        _ = await engine.CreateDatabaseAsync(tenant, cancellationToken);
                        var store = (await engine.OpenDatabaseAsync(tenant, cancellationToken))!;
                        var held = await store.BeginDurableTransactionAsync(cancellationToken);
                        Held.Add(held);
                    }
                    else if (parts[0] == "check")
                    {
                        var store = (await engine.OpenDatabaseAsync(tenant, cancellationToken))!;
                        await using var read = await store.BeginTransactionAsync(false, cancellationToken);
                        if (await read.GetOffsetAsync(cancellationToken) < 0)
                        {
                            throw new InvalidDataException("the recovered offset is invalid");
                        }
                    }
                    else
                    {
                        var store = (await engine.OpenDatabaseAsync(tenant, cancellationToken))!;
                        var ledger = engine.GetType().GetProperty("Ledger", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
                        await (Task)ledger.GetType().GetMethod("RecordIntentAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(ledger, [store, cancellationToken])!;
                        if (parts[0] == "rewrite")
                        {
                            var rewritten = engine.GetType().GetMethod("RewriteAsync")!.Invoke(engine, [tenant, 0L, cancellationToken])!;
                            await (Task)rewritten.GetType().GetMethod("AsTask")!.Invoke(rewritten, null)!;
                        }
                    }
                    await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                }
                catch (Exception error)
                {
                    await writer.WriteLineAsync(("error " + error).AsMemory(), cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("engine crash hook stopped");
        }
        catch (Exception error)
        {
            Console.WriteLine("engine crash hook failed: " + error);
        }
    }
}
