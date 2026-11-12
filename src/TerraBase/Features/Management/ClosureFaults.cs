using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Management;

internal sealed class ClosureFaults(IMeterFactory meters, TimeProvider time, ILogger<ClosureFaults> logger)
{
    private const string Failure = "failed";
    private static readonly TimeSpan LogEvery = TimeSpan.FromMinutes(10);
    private readonly Counter<long> _errors = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.project_closure.forward_errors");
    private readonly ConcurrentDictionary<(string? Region, string Error), DateTimeOffset> _logged = new();

    public void Failed(string? project, Exception error)
    {
        if (!Counted(null, Failure))
        {
            return;
        }

        if (project is null)
        {
            logger.ZLogWarning(error, $"The pass that sends project closures to the other regions failed; the next pass tries again");
        }
        else
        {
            logger.ZLogWarning(error, $"Sending the closure of project {project:@Project} to the other regions failed; it stays pending and is sent again");
        }
    }

    public void Unacknowledged(string project, RegionReply reply, string region)
    {
        var error = Label(reply.Answer);
        if (Counted(region, error))
        {
            logger.ZLogWarning(reply.Cause, $"Region {region:@Region} did not acknowledge the closure of project {project:@Project} ({error:@Error}); it stays pending and is sent again");
        }
    }

    public void Unanswered(string project, RegionReply reply, string region)
    {
        var error = Label(reply.Answer);
        if (Counted(region, error))
        {
            logger.ZLogWarning(reply.Cause, $"Region {region:@Region} did not answer the check before the closure of project {project:@Project} ({error:@Error}); nothing was written and the request is refused");
        }
    }

    private static string Label(RegionAnswer answer) => answer switch
    {
        RegionAnswer.NotEmpty => ManagementErrors.NotEmptyCode,
        RegionAnswer.ClosureConflict => ProjectClosure.ClosureConflictCode,
        RegionAnswer.BindingConflict => ProjectClosure.BindingConflictCode,
        RegionAnswer.PlatformProjectProtected => ManagementErrors.PlatformProjectProtectedCode,
        RegionAnswer.Refused => "refused",
        RegionAnswer.Invalid => "invalid_answer",
        RegionAnswer.NotListed => "not_listed",
        RegionAnswer.TimedOut => "timeout",
        RegionAnswer.Unreachable => "unreachable",
        RegionAnswer.Accepted or RegionAnswer.Pending or RegionAnswer.Empty => throw new ArgumentOutOfRangeException(nameof(answer), answer, "an acknowledged closure is not an error"),
        _ => throw new UnreachableException(),
    };

    private bool Counted(string? region, string error)
    {
        var tags = new TagList { { "error", error } };
        if (region is not null)
        {
            tags.Add("region", region);
        }

        _errors.Add(1, tags);
        var now = time.GetUtcNow();
        if (_logged.TryGetValue((region, error), out var last) && now - last < LogEvery)
        {
            return false;
        }

        _logged[(region, error)] = now;
        return true;
    }
}
