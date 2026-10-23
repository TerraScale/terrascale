package main

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"slices"
	"strings"
	"sync"
	"time"

	"terrascale.tech/ts-benchmark/internal/apibench"
	"terrascale.tech/ts-benchmark/internal/replbench"
	"terrascale.tech/ts-benchmark/internal/repllocal"
)

type sample struct {
	Repeat       int                               `json:"repeat"`
	Phase        string                            `json:"phase"`
	Metrics      map[string]float64                `json:"metrics"`
	Result       replbench.Result                  `json:"verification"`
	Visibility   map[string]replbench.Summary      `json:"dispatch_to_visible,omitempty"`
	Before       map[string]repllocal.Snapshot     `json:"before,omitempty"`
	After        map[string]repllocal.Snapshot     `json:"after,omitempty"`
	Load         []loadSample                      `json:"host_load"`
	CommitTiming map[string]repllocal.CommitTiming `json:"commit_timing,omitempty"`
}

type report struct {
	Host       string    `json:"host"`
	Commit     string    `json:"commit"`
	SourceTree string    `json:"terrabase_source_tree"`
	Profile    string    `json:"profile"`
	Scenario   string    `json:"scenario"`
	Docs       int       `json:"docs_per_phase"`
	Repeats    int       `json:"repeats"`
	Started    time.Time `json:"started"`
	Finished   time.Time `json:"finished"`
	HostInfo   string    `json:"host_info"`
	Samples    []sample  `json:"samples"`
	Error      string    `json:"error,omitempty"`
}

type loadSample struct {
	At       time.Time `json:"at"`
	Loadavg  string    `json:"loadavg"`
	Pressure string    `json:"cpu_pressure"`
}

func startLoadSampler() func() []loadSample {
	stop, done := make(chan struct{}), make(chan struct{})
	var samples []loadSample
	go func() {
		defer close(done)
		tick := time.NewTicker(time.Second)
		defer tick.Stop()
		for {
			load, _ := os.ReadFile("/proc/loadavg")
			pressure, _ := os.ReadFile("/proc/pressure/cpu")
			samples = append(samples, loadSample{At: time.Now().UTC(), Loadavg: strings.TrimSpace(string(load)), Pressure: strings.TrimSpace(string(pressure))})
			select {
			case <-stop:
				return
			case <-tick.C:
			}
		}
	}()
	return sync.OnceValue(func() []loadSample { close(stop); <-done; return samples })
}

func hostInfo() string {
	var b strings.Builder
	fmt.Fprintf(&b, "driver=%s cpus=%d\n", runtime.Version(), runtime.NumCPU())
	for _, args := range [][]string{{"uname", "-srmo"}, {"lscpu"}, {"dotnet", "--version"}, {"git", "status", "--porcelain", "--", "src", "tests/TerraBase.TestHarness"}} {
		out, err := exec.Command(args[0], args[1:]...).Output()
		if err != nil {
			fmt.Fprintf(&b, "%s: %v\n", args[0], err)
			continue
		}
		fmt.Fprintf(&b, "%s\n%s", strings.Join(args, " "), out)
	}
	return b.String()
}

func (r report) write(base string) error {
	if err := os.MkdirAll(filepath.Dir(base), 0o755); err != nil {
		return err
	}
	if err := repllocal.WriteJSON(base+".json", r); err != nil {
		return err
	}
	var b strings.Builder
	fmt.Fprintln(&b, "# Local replication baseline")
	fmt.Fprintf(&b, "\nHost: `%s`; profile: `%s`; commit: `%s`; TerraBase source tree: `%s`.\n", r.Host, r.Profile, r.Commit, r.SourceTree)
	fmt.Fprintf(&b, "\nStarted: %s. Repetitions: %d; source documents per write/feed phase: %d.\n", r.Started.Format(time.RFC3339), r.Repeats, r.Docs)
	if r.Scenario == "second-region" {
		fmt.Fprintln(&b, "\nTwo serving regions (gru/sjc), two authenticated OS processes, real SQLite commits and gRPC peers through loopback proxies. Each repetition uses a fresh cluster: 16 concurrent individual PUTs; seed 20261022; document mix 256 B 70%, 1 KiB 25%, 8 KiB 5%. Every loaded document is hash-checked on both replicas. The existing test bootstrap also places fra, but no fra process or alternate confirming region exists.")
	} else {
		fmt.Fprintln(&b, "\nOne source (gru), two replicas (sjc/fra), three OS processes, loopback peers through byte-counting proxies. Every phase uses a fresh cluster. Release, 16 concurrent requests, batch 1 or 16; deterministic seed 20261022; document mix 256 B 70%, 1 KiB 25%, 8 KiB 5%. Every phase checks every document against the driver's expected hash on every replica.")
	}
	fmt.Fprintln(&b, "\nMedian [minimum, maximum] across repetitions. Spread is the observed full range, not a confidence interval. Percentiles are per-request within a repetition; table medians do not pool histograms.")
	if r.Error != "" {
		fmt.Fprintf(&b, "\n**FAILED:** %s\n", r.Error)
	}
	fmt.Fprintln(&b, "\n| Phase | Metric | Repeats | Median | Min | Max |\n| --- | --- | ---: | ---: | ---: | ---: |")
	values := map[string][]float64{}
	for _, s := range r.Samples {
		for metric, value := range s.Metrics {
			key := s.Phase + " | " + metric
			values[key] = append(values[key], value)
		}
	}
	keys := make([]string, 0, len(values))
	for key := range values {
		keys = append(keys, key)
	}
	slices.Sort(keys)
	for _, key := range keys {
		xs := values[key]
		stat := apibench.NewStat(xs)
		fmt.Fprintf(&b, "| %s | %d | %.4f | %.4f | %.4f |\n", key, len(xs), stat.Median, stat.Min, slices.Max(xs))
	}
	if r.Scenario == "second-region" {
		fmt.Fprintln(&b, "\nLocal ACK measures server request dispatch through local durable commit, before remote confirmation starts. Remote ACK measures full HTTP request latency, including admission, local commit, replication, receiver commit and response. Dispatch-to-visible probes include polling and GET latency. None isolates fsync or WAN latency. See JSON for retries and all sample counts; no histogram pooling.")
		fmt.Fprintln(&b, "\nCommit-to-apply pairs each source CDC commit marker at the existing post-native-COMMIT checkpoint with that exact group's receiver post-native-FULL-COMMIT hook. Same-boot, same-time-namespace Linux CLOCK_MONOTONIC nanoseconds only; no wall-clock/HLC subtraction. All workload documents must match once. Document percentiles weight every document; group percentiles weight each commit once. Receiver transaction chunks are correlated before releasing their exclusive writer. Apply cleanup until sink return is reported separately; ACK return is excluded. Source marker-identification SELECT overhead remains inside lag and is reported separately. Exact timestamps and IDs are retained in JSON. Percentiles use nearest rank; component percentiles must not be added or subtracted.")
		fmt.Fprintln(&b, "\nPartition probes send each PUT once. After both-direction remote_durable probes, a test-only receiver gate holds ACK delivery after real apply commits. Both peer proxies are suspended; the pending write must answer 503 write_outcome_unknown without Retry-After, and its exact value must remain on both replicas. Both isolated regions must refuse new writes as 503 remote_region_unavailable with Retry-After, with no document on either replica. Healing must restore remote_durable success; only explicit pre-execution refusals are retried during healing. Gate/control files and tokens remain private and are never published.")
		fmt.Fprintln(&b, "\nThis is local process evidence, not a physical power-loss experiment, WAN latency result, backup-commit proof or production guarantee. No shared network settings or production resources change.\n\n```text\n"+r.HostInfo+"```")
		return os.WriteFile(base+".md", []byte(b.String()), 0o644)
	}
	fmt.Fprintln(&b, "\nCPU and allocation deltas come from the real System.Runtime Prometheus instruments (the same runtime counters exposed through EventPipe). sjc/fra receive the source workload; values include background work and, in write phases, low-rate visibility probes. Cost windows include load and a source replication barrier; exact listing verification runs afterward. Feed SELECT counters count instrumented SQL statements, including empty reads; they are not disk I/O. Feed windows start after admission, with no visibility probe writer. Requested 100 readers preserves the real admission cap; see admitted/rejected counts.")
	fmt.Fprintln(&b, "\nWire cost counts both directions of all peer TCP stream payload through the proxies, after application LZ4 encoding; includes gRPC/HTTP2 control and transport framing, excludes TCP/IP headers. Dispatch-to-visible probes poll concurrently with the write, so visibility may precede the remote-durable HTTP acknowledgement; resolution includes a 10 ms poll interval and GET latency. HTTP ack latency includes admission, local commit, remote durable confirmation and any retries. The old replbench post-ack lag is retained only as raw verification data.")
	fmt.Fprintln(&b, "\nVisibility PUT/GET requests retry HTTP 503 every 100 ms within a 30-second request deadline. Retry time remains in dispatch-to-visible lag; exhausted retries reject the run.")
	fmt.Fprintln(&b, "\nRaw repetitions, runtime counter scrapes, exact counts/digests, host topology and sampled host load/CPU pressure are in the adjacent JSON. WAN/netem is unsupported on this harness; no shared host network settings are changed.")
	fmt.Fprintln(&b, "\nCatch-up stops fra during exactly 100,000 acknowledged writes while gru/sjc retain remote durability. Rebuild measures empty-copy reconstruction with a fresh machine identity after exactly 1,000,000 documents, not certified fault recovery or side-copy salvage. Recovery verified-by time includes startup and full listing; first-exact-round time is a lower bound on that verification window.")
	fmt.Fprintln(&b, "\n```text\n"+r.HostInfo+"```")
	return os.WriteFile(base+".md", []byte(b.String()), 0o644)
}
