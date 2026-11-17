using System.Globalization;
using Microsoft.Extensions.Primitives;
using TerraBase.Features.Sql;
using TerraBase.Hosting;
using TerraBase.Features.Postgres;

namespace TerraBase.Features.Regions;

internal sealed class ProxyRequestBudget(TimeProvider time, TimeSpan duration, TimeSpan answer)
{
    internal const string RemainingHeader = "X-TerraBase-Remaining-Ms";
    internal const string AnswerHeader = "X-TerraBase-Answer-Ms";
    internal static readonly TimeSpan AnswerAllowance = TimeSpan.FromMilliseconds(250);
    private readonly long _started = time.GetTimestamp();

    internal TimeSpan Remaining => Left(duration);

    internal TimeSpan AnswerRemaining => Left(answer);

    internal string Milliseconds => Format(Remaining);

    internal string AnswerMilliseconds(TimeSpan roundTrip) => Format(Remaining - roundTrip - AnswerAllowance);

    private TimeSpan Left(TimeSpan budget) => TimeSpan.FromTicks(Math.Max(1, (budget - time.GetElapsedTime(_started)).Ticks));

    private static string Format(TimeSpan value) => Math.Max(1, (long)value.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

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
            var trusted = Listeners.IsForwarded(context)
                || Listeners.ArrivedOn(context) == TerraBaseListener.Internal
                    && context.Request.Path == PostgresRelay.Path
                    && context.RequestServices.GetRequiredService<InternalCredential>().Matches(context);
            var duration = trusted ? Within(SqlLimits.MaxRequestTime, context.Request.Headers[RemainingHeader]) : SqlLimits.MaxRequestTime;
            var answer = trusted ? Within(duration, context.Request.Headers[AnswerHeader]) : duration;
            context.Request.Headers.Remove(RemainingHeader);
            context.Request.Headers.Remove(AnswerHeader);
            context.Features.Set(new ProxyRequestBudget(context.RequestServices.GetRequiredService<TimeProvider>(), duration, answer));
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

    private static TimeSpan Within(TimeSpan limit, StringValues header) =>
        header is [var value] && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) && milliseconds > 0
            ? TimeSpan.FromMilliseconds(Math.Min(limit.TotalMilliseconds, milliseconds))
            : limit;
}
