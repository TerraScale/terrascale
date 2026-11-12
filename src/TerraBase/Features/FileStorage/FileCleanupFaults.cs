using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.FileStorage;

internal sealed class FileCleanupFaults(IMeterFactory meters, TimeProvider time, ILogger<FileCleanupFaults> logger)
{
    private static readonly TimeSpan LogEvery = TimeSpan.FromMinutes(10);
    private readonly Counter<long> _errors = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.file_storage.cleanup_errors");
    private readonly ConcurrentDictionary<(string Pass, string? Region, string Error), DateTimeOffset> _logged = new();

    public void Failed(string pass, string? project, string? region, Exception error)
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
            return;
        }

        _logged[(pass, region, kind)] = now;
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
}
