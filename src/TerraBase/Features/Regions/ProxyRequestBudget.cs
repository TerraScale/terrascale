using System.Globalization;
using TerraBase.Features.Sql;
using TerraBase.Hosting;
using TerraBase.Features.Postgres;

namespace TerraBase.Features.Regions;

internal sealed class ProxyRequestBudget(TimeProvider time, TimeSpan duration)
{
    internal const string RemainingHeader = "X-TerraBase-Remaining-Ms";
    private readonly long _started = time.GetTimestamp();

    internal TimeSpan Remaining => TimeSpan.FromTicks(Math.Max(1, (duration - time.GetElapsedTime(_started)).Ticks));

    internal string Milliseconds => Math.Max(1, (long)Remaining.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

    internal static ProxyRequestBudget For(HttpContext context) => context.Features.Get<ProxyRequestBudget>()
        ?? throw new InvalidOperationException("the proxy request budget is missing");

    internal CancellationTokenSource Link(CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(Remaining);
        return linked;
    }

    internal static WebApplication UseTerraBaseProxyBudget(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var duration = SqlLimits.MaxRequestTime;
            if ((Listeners.IsForwarded(context)
                    || Listeners.ArrivedOn(context) == TerraBaseListener.Internal
                        && context.Request.Path == PostgresRelay.Path
                        && context.RequestServices.GetRequiredService<InternalCredential>().Matches(context))
                && context.Request.Headers[RemainingHeader] is [var value]
                && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds)
                && milliseconds > 0)
            {
                duration = TimeSpan.FromMilliseconds(Math.Min(duration.TotalMilliseconds, milliseconds));
            }

            context.Request.Headers.Remove(RemainingHeader);
            context.Features.Set(new ProxyRequestBudget(context.RequestServices.GetRequiredService<TimeProvider>(), duration));
            if (!Listeners.IsForwarded(context) || context.Request.Method == "CONNECT")
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var original = context.RequestAborted;
            using var deadline = For(context).Link(original);
            context.RequestAborted = deadline.Token;
            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                context.RequestAborted = original;
            }
        });
        return app;
    }
}
