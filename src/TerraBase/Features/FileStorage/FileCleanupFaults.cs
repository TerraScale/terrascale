using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.FileStorage;

internal sealed class FileCleanupFaults(IMeterFactory meters, TimeProvider time, ILogger<FileCleanupFaults> logger)
{
    internal const string ClosureRegions = "closure_regions";
    private static readonly TimeSpan LogEvery = TimeSpan.FromMinutes(10);
    private readonly Counter<long> _errors = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.file_storage.cleanup_errors");
    private readonly ConcurrentDictionary<(string Pass, string? Region, string Error), DateTimeOffset> _logged = new();

    public void Failed(string pass, string? project, string? region, Exception error)
    {
        if (!Counted(pass, region, error))
        {
            return;
        }

        if (project is null)
        {
            logger.ZLogWarning(error, $"File storage cleanup pass {pass:@Pass} failed; the next pass tries again");
        }
        else if (region is null)
        {
            logger.ZLogWarning(error, $"File storage cleanup pass {pass:@Pass} failed for project {project:@Project}; its marker is kept and tried again");
        }
        else
        {
            logger.ZLogWarning(error, $"File storage cleanup pass {pass:@Pass} failed for project {project:@Project} in region {region:@Region}; its marker is kept and tried again");
        }
    }

    public void Unacknowledged(string project, string? region, Exception error)
    {
        if (!Counted(ClosureRegions, region, error))
        {
            return;
        }

        if (region is null)
        {
            logger.ZLogWarning(error, $"Sending the closure of project {project:@Project} to the other regions failed; it stays pending and is sent again");
        }
        else
        {
            logger.ZLogWarning(error, $"Region {region:@Region} did not acknowledge the closure of project {project:@Project}; it stays pending and is sent again");
        }
    }

    private bool Counted(string pass, string? region, Exception error)
    {
        var kind = error.GetType().Name;
        var tags = new TagList { { "pass", pass }, { "error", kind } };
        if (region is not null)
        {
            tags.Add("region", region);
        }

        _errors.Add(1, tags);
        var now = time.GetUtcNow();
        if (_logged.TryGetValue((pass, region, kind), out var last) && now - last < LogEvery)
        {
            return false;
        }

        _logged[(pass, region, kind)] = now;
        return true;
    }
}
