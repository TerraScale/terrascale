using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TerraScale.Dashboard.Common.Accounts;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Features.Logs;
using TerraScale.Dashboard.Features.Notifications;
using TerraScale.Dashboard.Platform.Http;
using TerraScale.Dashboard.Platform.Observability;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.Rpc;
using TerraScale.Dashboard.Platform.TsAuth;

internal static class BackgroundHost
{
    public static IHost Build(BackgroundOwner? owner = null, BackgroundCommands? commands = null, TimeProvider? clock = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(clock ?? TimeProvider.System);
        builder.Services.AddSingleton<IDashboardIdentity, DashboardIdentity>();
        builder.Services.AddDashboardStorage();
        builder.Services.AddAccountFeatures();
        builder.Services.AddSingleton<TerraBasePlanSync>();
        builder.Services.AddSingleton<BillingSchemaGate>();
        builder.Services.AddSingleton<StripeBilling>();
        builder.Services.AddSingleton<ServiceSloTelemetry>();
        builder.Services.AddSingleton<TerraBaseRpcClient>();
        builder.Services.AddSingleton<ILogDrainSource, LogDrainSource>();
        builder.Services.AddSingleton<LogDrains>();
        builder.Services.AddSingleton<IAlertSource, AlertSource>();
        builder.Services.AddSingleton<AlertRules>();
        builder.Services.AddSingleton<IAllowanceNotificationBackend, AllowanceNotificationBackend>();
        builder.Services.AddCustomerDelivery();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddHttpClient("platform", client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(PlatformTransport.CreateHandler);
        builder.Services.AddSingleton(services => new TsAuthClient(services.GetRequiredService<IHttpClientFactory>().CreateClient("platform")));
        builder.Services.AddSingleton<TsAuthGateway>();
        builder.Services.AddSingleton<ITsAuthPlatform>(services => services.GetRequiredService<TsAuthGateway>());
        builder.Services.AddSingleton<IPlatformRoleGateway>(services => services.GetRequiredService<TsAuthGateway>());
        builder.Services.RemoveAll<IHostedService>();
        if (owner is not null) { BackgroundStorage.Configure(builder.Services, owner, commands ?? new()); }
        return builder.Build();
    }
}

internal sealed class BackgroundClock(DateTimeOffset now) : TimeProvider
{
    private readonly Lock gate = new();
    private readonly List<ControlledTimer> timers = [];
    private long elapsed;
    public override DateTimeOffset GetUtcNow() => now;
    public override long GetTimestamp() => TimeProvider.System.GetTimestamp() + Interlocked.Read(ref elapsed);
    public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ControlledTimer(callback, state, dueTime, period);
        lock (gate) { timers.Add(timer); }
        return timer;
    }

    public void Advance(TimeSpan duration)
    {
        Interlocked.Add(ref elapsed, (long)(duration.TotalSeconds * TimestampFrequency));
        ControlledTimer[] active;
        lock (gate) { active = [.. timers]; }
        foreach (var timer in active) { timer.Advance(duration); }
    }

    private sealed class ControlledTimer(TimerCallback callback, object? state, TimeSpan due, TimeSpan period) : ITimer
    {
        private readonly Lock gate = new();
        private bool disposed;
        public bool Change(TimeSpan dueTime, TimeSpan newPeriod)
        {
            lock (gate)
            {
                if (disposed) { return false; }
                due = dueTime;
                period = newPeriod;
                return true;
            }
        }
        public void Advance(TimeSpan duration)
        {
            lock (gate)
            {
                if (disposed || due == Timeout.InfiniteTimeSpan) { return; }
                due -= duration;
                if (due > TimeSpan.Zero) { return; }
                due = period;
            }
            callback(state);
        }
        public void Dispose() { lock (gate) { disposed = true; } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
