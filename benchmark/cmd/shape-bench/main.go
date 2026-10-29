package main

import (
	"cmp"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"maps"
	"os"
	"os/exec"
	"os/signal"
	"path/filepath"
	"runtime"
	"slices"
	"strconv"
	"strings"
	"syscall"
	"time"

	"github.com/oklog/ulid/v2"
	"terrascale.tech/ts-benchmark/internal/apibench"
)

const (
	hostProject = "../tests/TerraBase.E2E"
	region      = "gru"
	hostRegions = "gru"
	reportDir   = "bench"

	writeRate = 20
	admitRate = 20

	admissionDeadline = 2 * time.Minute
	payloadBytes      = 256

	grace = 5 * time.Second
)

type target struct {
	name, transport, path string
	reads                 []readPath
	inferred              bool
	follow                func(*bench, context.Context, *measure, chan<- string)
	waitReady             func(*bench, context.Context, *measure, int64) (int64, error)
}

type readPath struct{ key, path string }

var targets = []target{
	{name: "changes.fanout", transport: "sse-http1", path: "/changes", inferred: true,
		reads:     []readPath{{key: "storage_reads_delta", path: "feed"}},
		follow:    (*bench).followChanges,
		waitReady: (*bench).waitStreams},

	{name: "shape.fanout", transport: "http1-long-poll", path: "/shape",
		reads:     []readPath{{key: "storage_reads_delta_shape", path: "shape"}, {key: "storage_reads_delta_shape_body", path: "shape_body"}, {key: "storage_reads_delta_point", path: "point"}},
		follow:    (*bench).followShape,
		waitReady: waitParked},
}

func (t target) paths() []string {
	var out []string
	for _, r := range t.reads {
		out = append(out, r.path)
	}
	return out
}

var followers = []int{1, 10, 100, 1000}

var spec = apibench.Spec{WarmupS: 5, StepS: 30, Safety: apibench.Safety{MaxErrorRate: 0.0001}}

func main() {
	run := run
	if len(os.Args) > 1 {
		switch os.Args[1] {
		case "load":
			run = func() error { return load(os.Args[2:]) }
		case "merge":
			run = func() error { return mergeCmd(os.Args[2:]) }
		case "catchup":
			run = func() error { return catchup(os.Args[2:]) }
		}
	}
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

	commits := provenance()
	work, err := os.MkdirTemp("", "shape-bench-")
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
	ready := apibench.Ready{Client: "shape-bench", SDK: "raw HTTP/1.1 (SSE and JSON long-poll)", SDKVersion: "-", Runtime: runtime.Version(),
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
	if runErr != nil {
		fmt.Fprintf(os.Stderr, "cluster host log tail:\n%s\n", tail(filepath.Join(work, "host.log")))
	}
	return runErr
}

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

func notes(results []apibench.Result, probes []probe, pid int, metricsURL, commits string) string {
	var b strings.Builder
	fmt.Fprintf(&b, "\n## Fan-out notes\n\n")
	fmt.Fprintf(&b, "- %s\n", commits)
	fmt.Fprintf(&b, "- One local node (%s) in the LocalBenchmarkHost process; writes and followers target %s. Plain HTTP on loopback: no WAN, no edge proxy, no TLS. No network probe ran; the startup row is one host launch until its targets file appeared.\n", hostRegions, region)
	fmt.Fprintln(&b, "- Shared reporter placeholders: network probe, startup max RSS, MB out, and auth ms are unmeasured here; their zeros are placeholders. The shared startup heading says 5 launches, but this run has one launch.")
	fmt.Fprintf(&b, "- Writer: %d PUTs/s of %d-byte documents into one collection, open loop; every write must succeed. Followers are opened one at a time at %d/s, each awaiting its admission or rejection (the local tenant is on the Hobby QoS plan), then all read together through the measured window. Per target: changes.fanout opens GET …/changes?cursor=now&collections=<that collection> as an SSE stream; shape.fanout opens GET …/shape as HTTP/1.1 JSON requests (bootstrap offset=-1, then snapshot pages, chunks and live long-polls).\n", writeRate, payloadBytes, admitRate)
	fmt.Fprintf(&b, "- Columns: conc = requested N; rate = writes/s; req/s = deliveries/s summed over admitted followers; p50…max = delivery lag from the write's send time (in the document) to the follower reading its change event; err %% = missed deliveries of in-window writes to admitted followers (grace %s); MB in = response body bytes read by admitted followers in the window (changes: SSE stream bytes; shape: JSON response bodies); CPU %% and RSS MB = the combined host process (/proc/%d: the node, ts-auth stand-in and test runner), not this client.\n", grace, pid)
	fmt.Fprintf(&b, "- QoS budget: the local tenant is on the Hobby plan (QosPolicy.cs: 50 requests/s sustained, burst 100, concurrency 4). The writer and every follower's request share it. A refusal is 429 tenant_busy with Retry-After; each is repeated after its Retry-After (at least 200 ms) and counted: qos_busy_followers (both targets) and qos_busy_writes. A repeat delays a delivery and is not a loss; a delivery that misses the window and grace is counted as missed.\n")
	fmt.Fprintf(&b, "- Admission, changes.fanout: the server admits at most 32 streams per database per machine and 1024 per machine (DatabaseChanges.cs); rejections are changes_busy. Above 32, only admitted followers are measured: req/s and MB in do not grow with requested N, and no gain past the cap is claimed.\n")
	fmt.Fprintf(&b, "- Admission, shape.fanout: a follower is admitted on its first 200 body. Refusals before admission are shape_busy (429, the running-flight caps: 32 per machine and 8 per organization (ShapeFlights.cs MaxFlights, MaxFlightsPerOrganization), no queue) and shape_too_large (503); anything else is an invalid measurement. Concurrent identical live polls join one flight. Live requesters are capped at 4096 per machine and 1024 per organization, across all flights (ShapeFlights.cs MaxRequesters, MaxRequestersPerOrganization); a live poll beyond that answers shape_busy and is retried. server_at_ready is the gauge terrabase.shape.requests.waiting when the step became ready (shape), or the open feed streams (changes). req/s and origin reads are the measured outcome, not an assumed collapse.\n")
	fmt.Fprintf(&b, "- Capacity rows: a step that reaches a limit is a row, not a failed run. Its stopped column names the limit: no follower admitted, the admission deadline, or a readiness timeout (all before the window, so writes, deliveries, storage reads and latency are n/a; the shared table's zeros there are placeholders, not measurements), or no delivery in the window (measured: every expected delivery is missed; its latency is n/a, since no delivery was measured, and the shared table's zeros there are placeholders). Rejected counts and qos_busy_followers are kept for every row.\n")
	fmt.Fprintf(&b, "- Storage reads (primary, measured): terrabase.storage.reads{path} (terrabase_storage_reads_total) from %s, after minus before the step, gru node only. Each target names its paths. The feed reports path=\"feed\": head, floor, prefix and retention reads, and each document body. The shape target reports path=\"shape\" (metadata SQL), path=\"shape_body\" (payload-body SELECTs) and path=\"point\" (document checks) as separate deltas, never summed here. The point delta is node-wide: it also counts the writer's point reads during the step, so it is not attributable to shape followers alone. Every count includes zero-row reads and misses; page cache hits count; this is not disk I/O.\n", metricsURL)
	fmt.Fprintf(&b, "- Storage window: the scrape before admission (before the first follower opens) through the scrape after warmup, the 30 s measured window, grace and a 6 s settle. A step's delta therefore includes admission, warmup and grace reads, not the measured window alone. A fall of a counter fails the step (reset or restarted node). A path absent before the step counts as zero, since its counter starts at zero in a process; a path absent after it fails the step, never as zero.\n")
	fmt.Fprintf(&b, "- Shape followers: each bootstraps at offset=-1 (307 to the newest snapshot's first page), pages the snapshot, catches up through chunks, then long-polls live at the sealed head. Their response bytes are JSON bodies over HTTP/1.1, not SSE; each request is one answer, and each follower holds at most one open live poll. Retries after shape_busy or unavailable are counted in the status \"retries\"; they are neither delivered nor missed.\n")
	fmt.Fprintf(&b, "- accounting_reads_delta (secondary): terrabase.stats.operations{kind=\"read\"} from %s, after minus before the step, gru node only. It is document accounting (document GETs and queries), published per closed 5 s window; it is not a storage read count.\n", metricsURL)
	fmt.Fprintf(&b, "- inferred_min_doc_selects (secondary, a source-based estimate, not measured; changes target only): gru feed only. TursoStorageTransaction.Feed.cs reads each upsert's current document with one SELECT per stream. The estimate counts received upserts of in-window writes through delivery grace, including redeliveries; warmup upserts, feed-table reads and other storage work are excluded. Compare it with the measured storage reads, not as equivalent to them.\n")
	fmt.Fprintf(&b, "\n| target | requested N | admitted | rejected | writes in window | delivered | missed | deliveries/s per admitted follower | storage reads delta (by path) | accounting reads delta (secondary) | inferred min doc SELECTs (secondary estimate) | stopped |\n| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\n")
	for _, r := range results {
		var rejected []string
		for k, v := range r.Status {
			if code, ok := strings.CutPrefix(k, "rejected_"); ok {
				rejected = append(rejected, fmt.Sprintf("%d %s", v, code))
			}
		}

		window := []string{"n/a", "n/a", "n/a", "n/a", "n/a", "n/a", "n/a"}
		if w, ok := r.Status["writes"]; ok {
			per := 0.0
			if a := r.Status["admitted"]; a > 0 {
				per = r.RPS / float64(a)
			}
			inferred := "n/a"
			if v, ok := r.Status["inferred_min_doc_selects"]; ok {
				inferred = strconv.FormatInt(v, 10)
			}
			window = []string{strconv.FormatInt(w, 10), strconv.FormatInt(r.OK, 10), strconv.FormatInt(r.Errors, 10), fmt.Sprintf("%.2f", per),
				storageCell(r.Status), strconv.FormatInt(r.Status["accounting_reads_delta"], 10), inferred}
		}
		fmt.Fprintf(&b, "| %s | %d | %d | %s | %s | %s |\n", r.Scenario, r.Concurrency, r.Status["admitted"], cmp.Or(strings.Join(rejected, ", "), "0"),
			strings.Join(window, " | "), cmp.Or(r.Stopped, "-"))
	}
	fmt.Fprintf(&b, "\n### Raw metric lines\n")
	for _, p := range probes {
		if p.after == "" {
			fmt.Fprintf(&b, "\n%s N=%d: no window or final scrape recorded; see the result's stopped reason or the client error for the outcome.\n", p.target, p.n)
			continue
		}
		fmt.Fprintf(&b, "\n%s N=%d before:\n\n```\n%s```\n\n%s N=%d after:\n\n```\n%s```\n", p.target, p.n, p.before, p.target, p.n, p.after)
	}
	return b.String()
}

func storageCell(status map[string]int64) string {
	var parts []string
	for _, k := range slices.Sorted(maps.Keys(status)) {
		rest, ok := strings.CutPrefix(k, "storage_reads_delta")
		if !ok {
			continue
		}
		if label := strings.TrimPrefix(rest, "_"); label == "" {
			parts = append(parts, strconv.FormatInt(status[k], 10))
		} else {
			parts = append(parts, fmt.Sprintf("%s %d", label, status[k]))
		}
	}
	return strings.Join(parts, "; ")
}

func git(args ...string) string {
	b, err := exec.Command("git", args...).Output()
	if err != nil {
		return "unknown"
	}
	return strings.TrimSpace(string(b))
}

func provenance() string {
	state := "unknown"
	if b, err := exec.Command("git", "status", "--porcelain", "--", "..").Output(); err == nil {
		state = map[bool]string{true: "clean", false: "dirty"}[len(b) == 0]
	}
	return fmt.Sprintf("Commit: %s; last product/host commit (src, tests): %s; last harness commit (benchmark/cmd/shape-bench): %s; working tree: %s",
		git("rev-parse", "HEAD"), git("log", "-1", "--format=%H", "--", "../src", "../tests"), git("log", "-1", "--format=%H", "--", "cmd/shape-bench"), state)
}
