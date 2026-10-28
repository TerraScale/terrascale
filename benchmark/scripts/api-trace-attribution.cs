using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace TraceAttribution
{
    /// <summary>
    /// Attributes one owned VsTurso process's EventPipe trace (benchmark/scripts/api-common-profile-20261028.sh): every sample-profiler
    /// thread sample or GC allocation tick is classified by its whole managed stack as server, client, mixed or runtime, then ranked by
    /// entry (root-most frame of its role), owner (deepest application frame) and leaf. Entry and owner rows partition their role and
    /// add up; inclusive rows do not. Samples are thread time (~1 ms per managed thread, running or not), not CPU.
    /// </summary>
    internal static partial class Program
    {
        private const string Sampler = "Microsoft-DotNETCore-SampleProfiler";
        private const string Runtime = "Microsoft-Windows-DotNETRuntime";

        private static int Main(string[] args)
        {
            if (args.Length != 4 || args[1] is not ("cpu" or "alloc" or "sampled"))
            {
                Console.Error.WriteLine("usage: TraceAttribution <trace.nettrace> cpu|alloc|sampled <owned pid> <threads.tsv>");
                return 2;
            }

            var cpu = args[1] == "cpu";
            var sampled = args[1] == "sampled";
            var pid = int.Parse(args[2], CultureInfo.InvariantCulture);
            var threads = new Dictionary<int, string>();
            foreach (var line in File.ReadLines(args[3]))
            {
                var parts = line.Split('\t');
                threads[int.Parse(parts[0], CultureInfo.InvariantCulture)] = TrailingDigits().Replace(parts[1], string.Empty).Trim();
            }

            using var trace = new TraceLog(TraceLog.CreateFromEventPipeDataFile(args[0]));
            var stacks = new Dictionary<CallStackIndex, Attributed>();
            var roles = new Tally();
            var byThread = new Tally();
            var entries = new Dictionary<string, Tally>(StringComparer.Ordinal);
            var owners = new Dictionary<string, Tally>(StringComparer.Ordinal);
            var leaves = new Dictionary<string, Tally>(StringComparer.Ordinal);
            var modules = new Dictionary<string, Tally>(StringComparer.Ordinal);
            var inclusive = new Dictionary<string, Tally>(StringComparer.Ordinal);
            var types = new Tally();
            var sizes = new Tally();
            var ticks = new Tally();
            var contention = new Tally();
            var exceptions = new Tally();
            var counters = new Dictionary<string, List<(double Time, double Value)>>(StringComparer.Ordinal);
            var perThread = new Dictionary<int, long>();
            var events = new Tally();
            var foreign = 0L;
            var unresolved = 0L;
            var instrumented = 0.0;
            double first = double.MaxValue, last = 0, contentionNs = 0, promoted0 = 0, gen1 = 0, gen2 = 0;
            var suspendAt = -1.0;
            var suspendForGc = false;
            var gcPauses = new List<double>();
            var gcSuspending = new List<double>();
            var otherPauses = new List<double>();
            var otherSuspending = new List<double>();
            var gcs = new long[3];
            foreach (var item in trace.Events)
            {
                events.Add(item.ProviderName + "/" + item.EventName, 1, false);
                if (item.ProcessID != pid)
                {
                    foreign++;
                    continue;
                }

                var name = item.EventName;
                if (item.ProviderName == Runtime)
                {
                    if (name == "GC/SuspendEEStart")
                    {
                        // Reasons 1 (GC) and 6 (GC preparation) belong to collections; the sample profiler's own suspensions do not.
                        suspendAt = item.TimeStampRelativeMSec;
                        suspendForGc = Convert.ToInt32(item.PayloadByName("Reason"), CultureInfo.InvariantCulture) is 1 or 6;
                    }
                    else if (name == "GC/SuspendEEStop" && suspendAt >= 0)
                    {
                        (suspendForGc ? gcSuspending : otherSuspending).Add(item.TimeStampRelativeMSec - suspendAt);
                    }
                    else if (name == "GC/RestartEEStop" && suspendAt >= 0)
                    {
                        (suspendForGc ? gcPauses : otherPauses).Add(item.TimeStampRelativeMSec - suspendAt);
                        suspendAt = -1;
                    }
                    else if (name == "GC/HeapStats")
                    {
                        promoted0 += Convert.ToDouble(item.PayloadByName("TotalPromotedSize0"), CultureInfo.InvariantCulture);
                        gen1 += Convert.ToDouble(item.PayloadByName("GenerationSize1"), CultureInfo.InvariantCulture);
                        gen2 += Convert.ToDouble(item.PayloadByName("GenerationSize2"), CultureInfo.InvariantCulture);
                    }
                    else if (name == "GC/Start")
                    {
                        gcs[Math.Clamp(Convert.ToInt32(item.PayloadByName("Depth"), CultureInfo.InvariantCulture), 0, 2)]++;
                    }
                    else if (name == "Contention/Stop" && item.PayloadByName("DurationNs") is { } duration)
                    {
                        contentionNs += Convert.ToDouble(duration, CultureInfo.InvariantCulture);
                    }
                    else if (name == "Contention/Start")
                    {
                        var waiter = Classify(trace, stacks, item);
                        contention.Add(waiter.Role + " | " + waiter.Owner, 1, false);
                    }
                    else if (name == "Exception/Start")
                    {
                        exceptions.Add(item.PayloadByName("ExceptionType") as string ?? "?", 1, false);
                    }
                }

                // EventCounters carry one unnamed struct field; its rendered form names the counter and its Increment or Mean.
                if (name == "EventCounters" && CounterName().Match(item.ToString()) is { Success: true } counter
                    && CounterValue().Match(item.ToString()) is { Success: true } value)
                {
                    var key = counter.Groups[1].Value;
                    if (!counters.TryGetValue(key, out var series))
                    {
                        series = [];
                        counters[key] = series;
                    }

                    series.Add((item.TimeStampRelativeMSec, double.Parse(value.Groups[2].Value, CultureInfo.InvariantCulture)));
                }

                double weight;
                bool managed;
                if (cpu && item.ProviderName == Sampler)
                {
                    weight = 1;
                    managed = Convert.ToInt32(item.PayloadByName("Type"), CultureInfo.InvariantCulture) == 2;
                    perThread[item.ThreadID] = perThread.GetValueOrDefault(item.ThreadID) + 1;
                }
                else if (!cpu && !sampled && name == "GC/AllocationTick")
                {
                    weight = Convert.ToDouble(item.PayloadByName("AllocationAmount64"), CultureInfo.InvariantCulture);
                    managed = true;
                }
                else if (sampled && name is "AllocationSampling" or "AllocationSampled")
                {
                    if (roles.Total == 0)
                    {
                        Console.WriteLine($"# sampled_payload {string.Join(',', item.PayloadNames)}");
                    }

                    // Randomized sampling with a 100 KiB mean distance: an object of size s is sampled with probability 1 - e^(-s/100 KiB),
                    // so each sample stands for 1/p objects and s/p bytes.
                    var bytes = Convert.ToDouble(item.PayloadByName("ObjectSize"), CultureInfo.InvariantCulture);
                    weight = bytes / -double.ExpM1(-bytes / 102_400);
                    managed = true;
                }
                else
                {
                    continue;
                }

                first = Math.Min(first, item.TimeStampRelativeMSec);
                last = Math.Max(last, item.TimeStampRelativeMSec);
                var sample = Classify(trace, stacks, item);
                unresolved += sample.Unresolved ? 1 : 0;
                instrumented += sample.Instrumented ? weight : 0;
                roles.Add(sample.Role, weight, managed);
                byThread.Add(threads.GetValueOrDefault(item.ThreadID, "(unnamed)") + " | " + sample.Role, weight, managed);
                Of(entries, sample.Role).Add(sample.Entry, weight, managed);
                Of(owners, sample.Role).Add(sample.Owner, weight, managed);
                Of(owners, sample.Role + " | leaf").Add(sample.Owner + " | " + sample.Leaf, weight, managed);
                Of(owners, sample.Role + " | caller").Add(sample.Owner + " <- " + sample.Caller, weight, managed);
                Of(leaves, sample.Role).Add(sample.Leaf, weight, managed);
                Of(modules, sample.Role).Add(sample.LeafModule, weight, managed);
                foreach (var frame in sample.Inclusive)
                {
                    Of(inclusive, sample.Role).Add(frame, weight, managed);
                }

                if (!cpu)
                {
                    var type = sample.Role + " | " + (item.PayloadByName("TypeName") as string ?? "?");
                    types.Add(type, weight, true);
                    var size = Convert.ToDouble(item.PayloadByName("ObjectSize"), CultureInfo.InvariantCulture);
                    sizes.Add(type, size, true);
                    ticks.Add(type, sampled ? weight / size : 1, true);
                }
            }

            if (foreign != 0 || roles.Total == 0)
            {
                Console.Error.WriteLine(FormattableString.Invariant($"Rejected: {foreign} events from another process, {roles.Total} weighted samples."));
                Print("Event (count)", events, events.Total, 40, 1, "count");
                return 1;
            }

            var span = (last - first) / 1000;
            var rate = Rate(counters, "total-requests");
            var requests = rate * span;
            // The sampler's real period: median samples per second over threads present for most of the span.
            var full = perThread.Values.Where(count => count >= 0.5 * perThread.Values.DefaultIfEmpty(0).Max()).Order().ToList();
            var perSecond = full.Count == 0 ? 0 : full[full.Count / 2] / span;
            var unit = cpu ? 1e6 / perSecond / requests : 1 / requests;
            Console.WriteLine(FormattableString.Invariant($"# {Path.GetFileName(args[0])} pid={pid} lost={trace.EventsLost} span_s={span:F3}"));
            Console.WriteLine(FormattableString.Invariant(
                $"# requests/s={rate:F0} requests~{requests:F0} weight={roles.Total:F0} samples/thread-s={perSecond:F1} unresolved_stacks={unresolved}"));
            Console.WriteLine(FormattableString.Invariant(
                $"# gc0/1/2={gcs[0]}/{gcs[1]}/{gcs[2]} promoted0_avg_B={promoted0 / Math.Max(1, gcs.Sum()):F0} gen1_avg_B={gen1 / Math.Max(1, gcs.Sum()):F0} gen2_avg_B={gen2 / Math.Max(1, gcs.Sum()):F0} contention_ms={contentionNs / 1e6:F2} instrumented_share={instrumented / roles.Total:P2}"));
            Console.WriteLine(Pauses("gc_pause", gcPauses, span));
            Console.WriteLine(Pauses("gc_suspending", gcSuspending, span));
            Console.WriteLine(Pauses("other_pause", otherPauses, span));
            Console.WriteLine(Pauses("other_suspending", otherSuspending, span));
            foreach (var (counter, series) in counters.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Console.WriteLine(FormattableString.Invariant($"# counter {counter} mean={series.Average(point => point.Value):F2} n={series.Count}"));
            }

            var per = cpu ? "est. thread-µs/request" : "est. B/request";
            Console.WriteLine(FormattableString.Invariant($"# mode={args[1]} samples={roles.Count("server") + roles.Count("client")} (server+client events)"));
            Print("Role", roles, roles.Total, 20, unit, per);
            Print("Thread class | role", byThread, roles.Total, 30, unit, per);
            foreach (var role in roles.Keys())
            {
                var total = roles.Weight(role);
                var depth = role is "server" ? 40 : 12;
                Print($"{role}: entry (root-most frame of the role)", entries[role], total, depth, unit, per);
                Print($"{role}: owner (deepest application frame)", owners[role], total, depth, unit, per);
                Print($"{role}: owner | leaf", owners[role + " | leaf"], total, depth, unit, per);
                Print($"{role}: owner <- calling application frame", owners[role + " | caller"], total, depth, unit, per);
                Print($"{role}: leaf method", leaves[role], total, depth, unit, per);
                Print($"{role}: leaf module", modules[role], total, 15, unit, per);
                if (role is "server" or "client")
                {
                    Print($"{role}: inclusive frame (not additive)", inclusive[role], total, role is "server" ? 90 : 25, unit, per);
                }
            }

            if (!cpu)
            {
                Print("Role | allocated type", types, roles.Total, 40, unit, per);
                Console.WriteLine();
                Console.WriteLine("## Top types: samples, mean sampled object size (B), estimated objects/request (sampled mode only)");
                Console.WriteLine();
                foreach (var (type, _, _) in types.Top(40))
                {
                    var samples = sampled ? sizes.Count(type) : ticks.Weight(type);
                    Console.WriteLine(FormattableString.Invariant(
                        $"| {type} | {samples:F0} | {sizes.Weight(type) / samples:F0} | {(sampled ? ticks.Weight(type) / requests : double.NaN):F2} |"));
                }
            }

            Print("Contention start: role | owner (count)", contention, contention.Total, 15, 1, "count");
            Print("Exception type (count)", exceptions, exceptions.Total, 10, 1, "count");
            Print("Event (count)", events, events.Total, 40, 1, "count");
            return 0;
        }

        private static Tally Of(Dictionary<string, Tally> tallies, string role)
        {
            if (!tallies.TryGetValue(role, out var tally))
            {
                tally = new Tally();
                tallies[role] = tally;
            }

            return tally;
        }

        /// <summary>Requests per second from a polled total: the slope between its first and last sample.</summary>
        private static double Rate(Dictionary<string, List<(double Time, double Value)>> counters, string name)
        {
            return !counters.TryGetValue(name, out var series) || series.Count < 2
                ? double.NaN
                : (series[^1].Value - series[0].Value) / ((series[^1].Time - series[0].Time) / 1000);
        }

        /// <summary>Count, total and quantiles of suspension intervals in milliseconds, and their share of the span.</summary>
        private static string Pauses(string name, List<double> pauses, double span)
        {
            pauses.Sort();
            double At(double quantile)
            {
                return pauses.Count == 0 ? 0 : pauses[Math.Min(pauses.Count - 1, (int)(quantile * pauses.Count))];
            }

            return FormattableString.Invariant(
                $"# {name} n={pauses.Count} total_ms={pauses.Sum():F1} share={pauses.Sum() / 1000 / span:P2} p50_ms={At(0.5):F3} p99_ms={At(0.99):F3} max_ms={At(1):F3}");
        }

        private static Attributed Classify(TraceLog trace, Dictionary<CallStackIndex, Attributed> cache, TraceEvent item)
        {
            var index = item.CallStackIndex();
            if (cache.TryGetValue(index, out var known))
            {
                return known;
            }

            var frames = new List<(string Module, string Name)>();
            for (var stack = index == CallStackIndex.Invalid ? null : trace.CallStacks[index]; stack is not null && frames.Count < 1024; stack = stack.Caller)
            {
                frames.Add((stack.CodeAddress.ModuleName ?? string.Empty, stack.CodeAddress.FullMethodName ?? string.Empty));
            }

            var server = frames.FindLastIndex(frame => IsServer(frame.Module, frame.Name));
            var client = frames.FindLastIndex(frame => IsClient(frame.Module, frame.Name));
            var role = (server, client) switch
            {
                ( >= 0, >= 0) => "mixed",
                ( >= 0, _) => "server",
                (_, >= 0) => "client",
                _ => RuntimeRole(frames),
            };
            var entry = server >= 0 && client < 0 ? Short(frames[server].Name)
                : client >= 0 && server < 0 ? Short(frames[client].Name)
                : frames.Count == 0 ? "(no managed frames)" : Short(frames[^1].Name);
            var app = frames.FindIndex(frame => IsApplication(frame.Module, frame.Name));
            var framework = frames.FindIndex(frame => IsServer(frame.Module, frame.Name) || IsClient(frame.Module, frame.Name));
            var owner = app >= 0 ? Short(frames[app].Name) : framework >= 0 ? "[framework] " + Short(frames[framework].Name) : "(BCL/runtime only)";
            // Up to four distinct application frames above the owner, so decorators do not hide the real caller.
            var callers = app < 0 ? [] : frames.Skip(app + 1).Where(frame => IsApplication(frame.Module, frame.Name))
                .Select(frame => Short(frame.Name)).Where(name => name != owner).Distinct(StringComparer.Ordinal).Take(4).ToList();
            var caller = callers.Count == 0 ? "(none)" : string.Join(" <- ", callers);
            var leaf = frames.Count == 0 ? "(no managed frames)" : Short(frames[0].Name);
            string[] inclusive = [.. frames.Select(frame => Short(frame.Name)).Distinct(StringComparer.Ordinal)];
            var attributed = new Attributed(
                role, entry, owner, caller, leaf, frames.Count == 0 ? "(none)" : frames[0].Module, inclusive,
                frames.Exists(frame => IsInstrumentation(frame.Name)), frames.Exists(frame => frame.Name.Length == 0));
            cache[index] = attributed;
            return attributed;
        }

        private static string RuntimeRole(List<(string Module, string Name)> frames)
        {
            bool Any(string part)
            {
                return frames.Exists(frame => frame.Name.Contains(part, StringComparison.Ordinal));
            }

            return frames.Count == 0 ? "runtime: no managed frames"
                : Any("SocketAsyncEngine") ? "runtime: socket event loop"
                : Any("PortableThreadPool") || Any("ThreadPoolWorkQueue") || Any("LowLevelLifoSemaphore") ? "runtime: thread pool, no app frame"
                : Any("TimerQueue") ? "runtime: timers"
                : "runtime: other";
        }

        // Server markers: the node's assemblies, ASP.NET Core and the harness's server-side doubles and instrumentation.
        private static bool IsServer(string module, string name)
        {
            return module == "terrabase" || module.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                || module.StartsWith("Microsoft.Extensions", StringComparison.Ordinal) || module.StartsWith("ZLogger", StringComparison.Ordinal)
                || (module.StartsWith("TerraScale.", StringComparison.Ordinal) && module != "TerraScale.DotNet")
                || name.StartsWith("TerraBase.Testing.", StringComparison.Ordinal) || IsInstrumentation(name);
        }

        // Client markers: HttpClient and the benchmark's request loop.
        private static bool IsClient(string module, string name)
        {
            return module == "System.Net.Http" || name.StartsWith("TerraBase.E2E.ApiTarget", StringComparison.Ordinal)
                || name.StartsWith("TerraBase.E2E.VsTursoBenchmark", StringComparison.Ordinal);
        }

        private static bool IsApplication(string module, string name)
        {
            return module == "terrabase" || (module.StartsWith("TerraScale.", StringComparison.Ordinal) && module != "TerraScale.DotNet")
                || name.StartsWith("TerraBase.", StringComparison.Ordinal);
        }

        private static bool IsInstrumentation(string name)
        {
            return name.StartsWith("TerraBase.E2E.ServerAttribution", StringComparison.Ordinal)
                || name.StartsWith("TerraBase.E2E.AttributedDocuments", StringComparison.Ordinal);
        }

        /// <summary>A readable method name: no parameters, generic instantiations or arity, async state machines as their method.</summary>
        private static string Short(string name)
        {
            if (name.Length == 0)
            {
                return "(unresolved)";
            }

            var open = name.IndexOf('(', StringComparison.Ordinal);
            var shorter = open > 0 ? name[..open] : name;
            string previous;
            do
            {
                previous = shorter;
                shorter = Brackets().Replace(shorter, string.Empty);
            }
            while (shorter != previous);

            shorter = Arity().Replace(shorter, string.Empty);
            shorter = AsyncMoveNext().Replace(shorter, ".$1 (async)");
            return shorter.Replace("|", "/", StringComparison.Ordinal);
        }

        private static void Print(string title, Tally tally, double denominator, int top, double unit, string per)
        {
            Console.WriteLine();
            Console.WriteLine($"## {title}");
            Console.WriteLine();
            Console.WriteLine($"| Key | Weight | Share | Managed share | {per} |");
            Console.WriteLine("| --- | ---: | ---: | ---: | ---: |");
            foreach (var (key, weight, managed) in tally.Top(top))
            {
                Console.WriteLine(FormattableString.Invariant(
                    $"| {key} | {weight:F0} | {weight / denominator:P2} | {(weight == 0 ? 0 : managed / weight):P1} | {weight * unit:F2} |"));
            }
        }

        [GeneratedRegex(@"\[[^\[\]]*\]")]
        private static partial Regex Brackets();

        [GeneratedRegex("&quot;Name&quot;:&quot;([^&]+)&quot;")]
        private static partial Regex CounterName();

        [GeneratedRegex("&quot;(Increment|Mean)&quot;:([-0-9.Ee+]+)")]
        private static partial Regex CounterValue();

        [GeneratedRegex(@"`\d+")]
        private static partial Regex Arity();

        [GeneratedRegex(@"\+<([^>]+)>d__\d+\.MoveNext$")]
        private static partial Regex AsyncMoveNext();

        [GeneratedRegex(@"[\d ]+$")]
        private static partial Regex TrailingDigits();
    }

    /// <summary>One stack's classification, cached by its call-stack index.</summary>
    internal sealed record Attributed(string Role, string Entry, string Owner, string Caller, string Leaf, string LeafModule, IReadOnlyList<string> Inclusive, bool Instrumented, bool Unresolved);

    /// <summary>Weights per key, with the managed (running managed code) part of each.</summary>
    internal sealed class Tally
    {
        private readonly Dictionary<string, (double Weight, double Managed)> _rows = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);

        public double Total { get; private set; }

        public void Add(string key, double weight, bool managed)
        {
            var (sum, running) = _rows.GetValueOrDefault(key);
            _rows[key] = (sum + weight, running + (managed ? weight : 0));
            _counts[key] = _counts.GetValueOrDefault(key) + 1;
            Total += weight;
        }

        public long Count(string key)
        {
            return _counts.GetValueOrDefault(key);
        }

        public double Weight(string key)
        {
            return _rows.GetValueOrDefault(key).Weight;
        }

        public IEnumerable<string> Keys()
        {
            return _rows.OrderByDescending(pair => pair.Value.Weight).Select(pair => pair.Key);
        }

        public IEnumerable<(string Key, double Weight, double Managed)> Top(int count)
        {
            return _rows.OrderByDescending(pair => pair.Value.Weight).Take(count).Select(pair => (pair.Key, pair.Value.Weight, pair.Value.Managed));
        }
    }
}
