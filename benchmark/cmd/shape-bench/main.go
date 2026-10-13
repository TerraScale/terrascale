// Command shape-bench measures the changes feed's fan-out on one of two local TerraBase nodes: a steady writer
// at a fixed rate into one collection, and N = 1, 10, 100 and 1000 followers of the same database's
// GET …/changes SSE stream, one N
// per step. It records requested, admitted and rejected followers (the server admits 32 streams per database and 1024
// per machine), delivery lag, SSE body bytes, the server's CPU and memory and its document accounting reads.
//
// Run it from benchmark/ after a Release build of the host (README.md). It takes no flags.
package main

import (
	"cmp"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"os/exec"
	"os/signal"
	"runtime"
	"strings"
	"syscall"
	"time"

	"github.com/oklog/ulid/v2"
	"terrascale.tech/ts-benchmark/internal/apibench"
)

const (
	// Database placement requires two regions; all client traffic goes to gru.
	hostProject = "../tests/TerraBase.E2E"
	region      = "gru"
	hostRegions = "gru,sjc"
	reportDir   = "bench"

	writeRate = 20 // writes per second, open loop
	admitRate = 20 // follower requests per second while a step opens its streams
	// 1000 paced followers take about 50 s; past this the step is invalid.
	admissionDeadline = 2 * time.Minute
	payloadBytes      = 256
	// Deliveries of in-window writes that arrive within this long after the window still count.
	grace = 5 * time.Second
)

// target is one followed endpoint below the database URL; the report names its steps by name and transport.
type target struct {
	name, transport, path string
	follow                func(*bench, context.Context, *measure, chan<- string)
	waitReady             func(*bench, context.Context, int64) error
}

// targets is the fixed target list, each run at every follower count.
var targets = []target{{name: "changes.fanout", transport: "sse-http1", path: "/changes",
	follow: (*bench).followChanges, waitReady: (*bench).waitStreams}}

// followers are the follower counts: every N runs, even past the admission cap.
var followers = []int{1, 10, 100, 1000}

// spec carries the step timing; any missed delivery marks a step's error rate. One-step sweeps have no p99 baseline,
// so there is no p99 gate.
var spec = apibench.Spec{WarmupS: 5, StepS: 30, Safety: apibench.Safety{MaxErrorRate: 0.0001}}

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, "shape-bench:", err)
		os.Exit(1)
	}
}

func run() error {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	hostname, err := os.Hostname()
	if err != nil {
		return err
	}
	// Before the run writes anything, so the report's own files never count as changes.
	commits := provenance()
	work, err := os.MkdirTemp("", "shape-bench-") // 0700: the targets file holds the node's token
	if err != nil {
		return err
	}
	defer os.RemoveAll(work)

	start := time.Now()
	h, err := startHost(ctx, work)
	if err != nil {
		return err
	}
	defer h.stop()
	startup := apibench.Startup{Type: "startup", InitMS: float64(time.Since(start).Milliseconds())}
	setup := time.Now()
	b, err := newBench(ctx, h.targets)
	if err != nil {
		return err
	}
	ready := apibench.Ready{Client: "shape-bench", SDK: "raw HTTP/1.1 SSE", SDKVersion: "-", Runtime: runtime.Version(),
		Database: h.targets.Database, SetupMS: float64(time.Since(setup).Milliseconds())}

	var probes []probe
	got, results, runErr := sweep(startup, ready, func(st apibench.Step) (apibench.Result, error) {
		res, p, err := b.step(ctx, st)
		probes = append(probes, p)
		return res, err
	})
	cr := apibench.ClientRun{Client: ready.Client, Ready: &ready, Results: results, Done: &apibench.Done{Type: "done", Cleanup: true}}
	if runErr != nil {
		cr.Error = runErr.Error()
	}
	if err := h.stop(); err != nil {
		cr.Done = &apibench.Done{Type: "done", CleanupError: err.Error()}
		runErr = cmp.Or(runErr, fmt.Errorf("stop host: %w", err))
	}
	r := apibench.Run{
		ID: ulid.Make().String(), Host: hostname, ClusterState: "local-" + hostRegions, Endpoint: h.targets.Regions[region].URL,
		Start: start.UTC(), End: time.Now().UTC(),
		Startup: []apibench.StartupStat{{Client: "local terrabase host (1 launch)", WallMS: apibench.NewStat([]float64{got.InitMS}),
			InitMS: apibench.NewStat([]float64{got.InitMS})}},
		Clients: []apibench.ClientRun{cr},
	}
	text := notes(results, probes, h.targets.PID, b.metrics, commits)
	md, js, err := apibench.WriteReport(reportDir, r)
	if err != nil {
		return cmp.Or(runErr, err)
	}
	f, err := os.OpenFile(md, os.O_WRONLY|os.O_APPEND, 0)
	if err != nil {
		return cmp.Or(runErr, err)
	}
	if _, err := f.WriteString(text); err != nil {
		f.Close()
		return cmp.Or(runErr, err)
	}
	if err := f.Close(); err != nil {
		return cmp.Or(runErr, err)
	}
	fmt.Println(md)
	fmt.Println(js)
	return runErr
}

// sweep runs each target at each N as its own one-step apibench sweep through apibench.Serve, so a breached threshold
// at one N never skips the next. The serving side announces startup before Serve's ready line. Any exec error (an
// invalid measurement) ends the run with the results so far.
func sweep(startup apibench.Startup, ready apibench.Ready, step func(apibench.Step) (apibench.Result, error)) (apibench.Startup, []apibench.Result, error) {
	stepsR, stepsW := io.Pipe()
	resultsR, resultsW := io.Pipe()
	defer stepsW.Close()
	errs := make(chan error, 1)
	served := make(chan error, 1)
	go func() {
		err := json.NewEncoder(resultsW).Encode(startup)
		if err == nil {
			err = apibench.Serve(stepsR, resultsW, ready, func(st apibench.Step) apibench.Result {
				res, err := step(st)
				errs <- err
				return res
			})
		}
		resultsW.CloseWithError(err)
		served <- err
	}()
	var got apibench.Startup
	dec := json.NewDecoder(resultsR)
	if err := dec.Decode(&got); err != nil {
		return got, nil, err
	}
	if err := dec.Decode(&apibench.Ready{}); err != nil {
		return got, nil, err
	}
	enc := json.NewEncoder(stepsW)
	exec := func(st apibench.Step) (apibench.Result, error) {
		var res apibench.Result
		if err := enc.Encode(st); err != nil {
			return res, err
		}
		if err := dec.Decode(&res); err != nil {
			return res, err
		}
		return res, <-errs
	}
	var out []apibench.Result
	for _, t := range targets {
		for _, n := range followers {
			sw := apibench.Sweep{Scenario: t.name, Transport: t.transport, PayloadBytes: payloadBytes, Concurrency: []int{n}}
			rs, err := apibench.RunSweep(sw, spec, exec)
			if err != nil {
				return got, out, fmt.Errorf("%s N=%d: %w", t.name, n, err)
			}
			out = append(out, rs...)
		}
	}
	stepsW.Close()
	return got, out, <-served
}

// notes is the report's own section: what each column means for this benchmark, the admission outcome per N and the
// raw metric lines the accounting deltas come from.
func notes(results []apibench.Result, probes []probe, pid int, metricsURL, commits string) string {
	var b strings.Builder
	fmt.Fprintf(&b, "\n## Fan-out notes\n\n")
	fmt.Fprintf(&b, "- %s\n", commits)
	fmt.Fprintf(&b, "- Two local nodes (%s) in the tests/TerraBase.E2E BenchmarkClusterHost process: placement requires two regions; writes and followers target only %s. Plain HTTP on loopback: no WAN, no Fly Proxy, no TLS. No network probe ran; the startup row is one host launch until its targets file appeared.\n", hostRegions, region)
	fmt.Fprintln(&b, "- Shared reporter placeholders: network probe, startup max RSS, MB out, and auth ms are unmeasured here; their zeros are placeholders. The shared startup heading says 5 launches, but this run has one launch.")
	fmt.Fprintf(&b, "- Writer: %d PUTs/s of %d-byte documents into one collection, open loop; every write must succeed. Followers: GET …/changes?cursor=now&collections=<that collection>, opened one at a time at %d/s, each awaiting its admission or rejection (the local tenant is on the Hobby QoS plan), then all read together through the measured window.\n", writeRate, payloadBytes, admitRate)
	fmt.Fprintf(&b, "- Columns: conc = requested N; rate = writes/s; req/s = deliveries/s summed over admitted followers; p50…max = delivery lag from the write's send time (in the document) to the follower reading its change event; err %% = missed deliveries of in-window writes to admitted followers (grace %s); MB in = SSE response body bytes of admitted streams in the window; CPU %% and RSS MB = the combined host process (/proc/%d: both nodes, replication work, ts-auth stand-in and test runner), not this client.\n", grace, pid)
	fmt.Fprintf(&b, "- Admission: the server admits at most 32 streams per database per machine and 1024 per machine (DatabaseChanges.cs). Above 32, only admitted followers are measured: req/s and MB in do not grow with requested N, and no gain past the cap is claimed.\n")
	fmt.Fprintf(&b, "- accounting_reads_delta: terrabase.stats.operations{kind=\"read\"} from %s, after minus before the step, gru node only; sjc reads are not included. It is document accounting (document GETs and queries), published per closed 5 s window; it is not a count of storage reads, and the feed does not add to it.\n", metricsURL)
	fmt.Fprintf(&b, "- Origin physical storage reads of the feed: N/A, not instrumented. inferred_min_doc_selects is inferred from source, not measured, gru feed only; sjc reads are not included: SqliteStorageTransaction.Feed.cs reads each upsert's current document with one SELECT per stream. The inference counts received upserts of in-window writes through delivery grace, including redeliveries; warmup upserts, feed-table page reads and other storage work are excluded.\n")
	fmt.Fprintf(&b, "\n| target | requested N | admitted | rejected | writes in window | delivered | missed | deliveries/s per admitted follower | accounting reads delta | inferred min doc SELECTs for in-window writes |\n| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\n")
	for _, r := range results {
		var rejected []string
		for k, v := range r.Status {
			if code, ok := strings.CutPrefix(k, "rejected_"); ok {
				rejected = append(rejected, fmt.Sprintf("%d %s", v, code))
			}
		}
		per := 0.0
		if a := r.Status["admitted"]; a > 0 {
			per = r.RPS / float64(a)
		}
		fmt.Fprintf(&b, "| %s | %d | %d | %s | %d | %d | %d | %.2f | %d | %d |\n", r.Scenario, r.Concurrency, r.Status["admitted"], cmp.Or(strings.Join(rejected, ", "), "0"),
			r.Status["writes"], r.OK, r.Errors, per, r.Status["accounting_reads_delta"], r.Status["inferred_min_doc_selects"])
	}
	fmt.Fprintf(&b, "\n### Raw metric lines\n")
	for _, p := range probes {
		fmt.Fprintf(&b, "\n%s N=%d before:\n\n```\n%s```\n\n%s N=%d after:\n\n```\n%s```\n", p.target, p.n, p.before, p.target, p.n, p.after)
	}
	return b.String()
}

func git(args ...string) string {
	b, err := exec.Command("git", args...).Output()
	if err != nil {
		return "unknown"
	}
	return strings.TrimSpace(string(b))
}

// provenance names the measured code: HEAD, the last commit of the product and host (src, tests) and of this harness,
// and whether the working tree differs from HEAD.
func provenance() string {
	state := "unknown"
	if b, err := exec.Command("git", "status", "--porcelain", "--", "..").Output(); err == nil {
		state = map[bool]string{true: "clean", false: "dirty"}[len(b) == 0]
	}
	return fmt.Sprintf("Commit: %s; last product/host commit (src, tests): %s; last harness commit (benchmark/cmd/shape-bench): %s; working tree: %s",
		git("rev-parse", "HEAD"), git("log", "-1", "--format=%H", "--", "../src", "../tests"), git("log", "-1", "--format=%H", "--", "cmd/shape-bench"), state)
}
