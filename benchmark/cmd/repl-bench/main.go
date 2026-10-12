// Command repl-bench writes documents across every region of one TerraBase database, measures write latency and
// throughput per region, replication lag per region pair and the time to full convergence, verifies every region
// document by document, and records CPU, memory, swap and disk per node.
//
//	repl-bench --targets cluster.json --docs 1000000 --out bench
//
// The targets file comes from the local cluster host (make benchmark-repl-local) or, for Fly, from
// benchmark/scripts/repl-fly.sh. A multi-machine run uses --step: setup once, load on one machine per region (--only),
// then verify with every region's partial.
package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"flag"
	"fmt"
	"log"
	"os"
	"os/exec"
	"os/signal"
	"path/filepath"
	"strings"
	"syscall"
	"time"

	"terrascale.tech/ts-benchmark/internal/replbench"
)

func main() {
	var (
		targetsPath = flag.String("targets", "", "targets JSON (required)")
		name        = flag.String("name", "", "report name (default repl-<kind>-<docs>)")
		out         = flag.String("out", "bench", "report directory")
		docs        = flag.Int("docs", 1_000_000, "documents created across every region")
		sizes       = flag.String("sizes", "256:70,1024:25,8192:5", "document sizes bytes:weight")
		batch       = flag.Int("batch", 1, "documents per request (1 = PUT, more = one transaction)")
		conc        = flag.Int("concurrency", 16, "writers per region")
		deletePct   = flag.Float64("delete-pct", 1, "percent of created documents deleted right after by their region")
		conflicts   = flag.Int("conflict-docs", 200, "documents every region overwrites concurrently")
		rounds      = flag.Int("conflict-rounds", 5, "overwrites per conflict document per region")
		probeEvery  = flag.Duration("probe-every", 250*time.Millisecond, "lag probe period per origin region (0 = none)")
		converge    = flag.Duration("converge", 30*time.Minute, "longest wait for every region to match after the load")
		seed        = flag.Uint64("seed", uint64(time.Now().UnixNano()), "content seed")
		notTested   = flag.String("not-tested", "", "extra not-tested lines, separated by ';'")
		step        = flag.String("step", "all", "all, or one step of a multi-machine run: setup, load, verify, or resources (adds Fly machine metrics to --report)")
		reportPath  = flag.String("report", "", "resources step: the .json of a verify step's report")
		statePath   = flag.String("state", "state.json", "setup's output and the load and verify steps' input")
		only        = flag.String("only", "", "load step: the one region this machine writes to")
		partialOut  = flag.String("partial", "", "load step: where this region's partial goes")
		partials    = flag.String("partials", "", "verify step: every region's partial, separated by ','")
		commitFlag  = flag.String("commit", "", "commit to record (default: git rev-parse HEAD)")
	)
	flag.Parse()
	logf := log.New(os.Stderr, "repl-bench ", log.LstdFlags|log.Lmsgprefix).Printf
	if *step == "resources" {
		addResources(*reportPath, logf)
		return
	}
	if *targetsPath == "" {
		log.Fatal("--targets is required")
	}
	t, err := replbench.LoadTargets(*targetsPath)
	if err != nil {
		log.Fatal(err)
	}
	sw, err := replbench.ParseSizes(*sizes)
	if err != nil {
		log.Fatal(err)
	}
	if *batch < 1 || *batch > 1024 || *conc < 1 || *docs < 1 {
		log.Fatal("--batch must be 1..1024, --concurrency and --docs at least 1")
	}
	cfg := replbench.Config{Docs: *docs, Sizes: sw, Batch: *batch, Concurrency: *conc, DeletePct: *deletePct, ConflictDocs: *conflicts,
		ConflictRounds: *rounds, ProbeEvery: *probeEvery, Converge: *converge, Seed: *seed, Only: *only}
	if (*step == "load") != (*only != "") {
		log.Fatal("--only goes with --step load, and --step load needs --only")
	}
	if *commitFlag == "" {
		*commitFlag = commit()
	}
	if *name == "" {
		*name = fmt.Sprintf("repl-%s-%d", t.Kind, *docs)
	}
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()

	host, _ := os.Hostname()
	res := replbench.Result{Name: *name, Kind: t.Kind, Commit: *commitFlag, WorkingTree: workingTree(), Host: host, Start: time.Now(), Machines: t.Machines,
		TerraBaseApp: t.App, TerraBaseOrg: t.Org,
		Config: map[string]any{"docs": *docs, "sizes": *sizes, "batch": *batch, "concurrency_per_region": *conc, "delete_pct": *deletePct,
			"conflict_docs": *conflicts, "conflict_rounds": *rounds, "probe_every": probeEvery.String(), "seed": *seed}}
	r, err := replbench.NewRunner(cfg, t, logf)
	if err != nil {
		log.Fatal(err)
	}
	fail := func(stage string, err error) {
		res.Errors = append(res.Errors, stage+": "+err.Error())
		logf("%s: %v", stage, err)
	}

	switch *step {
	case "setup":
		if err := r.Setup(ctx); err != nil {
			log.Fatal(err)
		}
		if err := replbench.WriteJSON(*statePath, r.State()); err != nil {
			log.Fatal(err)
		}
		return
	case "load":
		var state replbench.State
		if err := replbench.ReadJSON(*statePath, &state); err != nil {
			log.Fatal(err)
		}
		r.Attach(state)
		start := time.Now()
		elapsed, err := r.Load(ctx)
		if err := replbench.WriteJSON(*partialOut, r.Partial(start, elapsed.Seconds())); err != nil {
			log.Fatal(err)
		}
		if err != nil {
			log.Fatal(err)
		}
		logf("%s loaded in %.1f s", *only, elapsed.Seconds())
		return
	case "verify", "all":
	default:
		log.Fatalf("--step %q: want all, setup, load or verify", *step)
	}

	var sampler *replbench.LocalSampler
	if t.Kind == "local" && t.PID > 0 {
		sampler = replbench.StartLocalSampler(t.PID, t.DataDirs)
	}
	var loadSecs float64
	var loadEnd time.Time
	var parts []replbench.Partial
	if *step == "verify" {
		var state replbench.State
		if err := replbench.ReadJSON(*statePath, &state); err != nil {
			log.Fatal(err)
		}
		r.Attach(state)
		for path := range strings.SplitSeq(*partials, ",") {
			var p replbench.Partial
			if err := replbench.ReadJSON(path, &p); err != nil {
				log.Fatal(err)
			}
			parts = append(parts, p)
			if p.End.After(loadEnd) {
				loadEnd = p.End
			}
		}
		if err := r.Merge(parts); err != nil {
			log.Fatal(err)
		}
		if res.Convergence, err = r.Converge(ctx, loadEnd); err != nil {
			fail("converge", err)
		}
		if res.Conflicts, err = r.CheckConflicts(ctx); err != nil {
			fail("conflicts", err)
		}
	} else if err := r.Setup(ctx); err != nil {
		fail("setup", err)
	} else {
		elapsed, err := r.Load(ctx)
		loadSecs, loadEnd = elapsed.Seconds(), time.Now()
		if err != nil {
			fail("load", err)
		}
		logf("load done in %.1f s", loadSecs)
		if res.Convergence, err = r.Converge(ctx, loadEnd); err != nil {
			fail("converge", err)
		}
		if res.Conflicts, err = r.CheckConflicts(ctx); err != nil {
			fail("conflicts", err)
		}
	}
	r.Collect(&res, loadSecs)
	if parts != nil {
		replbench.FromPartials(&res, parts)
		res.Start = res.Start.Add(-time.Duration(res.LoadSeconds * float64(time.Second))) // the run began with the load
	}
	res.End = time.Now()
	switch {
	case sampler != nil:
		res.Resources = sampler.Stop()
	case t.Kind == "fly":
		// On a Fly machine there is no Fly token: the operator adds the metrics afterwards with --step resources.
		res.Resources = replbench.Resources{Source: "Fly Prometheus API", Note: "not read yet: repl-bench --step resources adds them"}
		if token := os.Getenv("FLY_API_TOKEN"); token != "" {
			if r, err := replbench.FlyResources(context.WithoutCancel(ctx), t.Org, t.App, token, res.Start, res.End); err == nil {
				res.Resources = r
			} else {
				res.Resources.Note = "machine metrics could not be read: " + err.Error()
			}
		}
	}
	res.NotTested = notTestedLines(t, cfg, *notTested)
	path, err := res.Write(*out)
	if err != nil {
		log.Fatal(err)
	}
	logf("report %s", path)
	if !res.Passed() {
		os.Exit(1)
	}
}

func commit() string {
	b, err := exec.Command("git", "rev-parse", "HEAD").Output()
	if err != nil {
		return "unknown"
	}
	return strings.TrimSpace(string(b))
}

// workingTree is "clean", or the SHA-256 of the tracked changes plus the untracked files' hashes, so a report names the
// exact code it measured even when it was not committed.
func workingTree() string {
	diff, err := exec.Command("git", "diff", "HEAD", "--binary").Output()
	if err != nil {
		return "unknown"
	}
	untracked, _ := exec.Command("sh", "-c", "git ls-files --others --exclude-standard -z | xargs -0 -r sha256sum").Output()
	if len(diff) == 0 && len(untracked) == 0 {
		return "clean"
	}
	sum := sha256.Sum256(append(append(diff, '\n'), untracked...))
	return hex.EncodeToString(sum[:])
}

func notTestedLines(t replbench.Targets, cfg replbench.Config, extra string) []string {
	lines := []string{
		"reads under load: the load is writes only (creates, deletes and conflict patches); reads happen only in probes and verification",
		"counters, sets, SQL writes and the changes feed: not part of this load",
		"faults (partitions, a region down, restarts, clock skew) during the load: covered by tests/TerraBase.E2E, not here",
	}
	if t.Kind == "local" {
		lines = append(lines,
			"network: the regions are in-process nodes on one machine, so latency and lag carry no WAN round trips",
			"per-node CPU and memory: the nodes share one host process; only disk is per node")
	}
	if cfg.Batch == 1 {
		lines = append(lines, "transactions: every document is its own PUT")
	}
	for l := range strings.SplitSeq(extra, ";") {
		if l = strings.TrimSpace(l); l != "" {
			lines = append(lines, l)
		}
	}
	return lines
}

// addResources reads a report the verify step wrote on a Fly machine, adds every TerraBase machine's metrics for the run
// from Fly's Prometheus API (FLY_API_TOKEN, read only) and writes the report again. It runs on the operator host, which
// holds the Fly token, while the benchmark key stays on the Fly machines.
func addResources(path string, logf func(string, ...any)) {
	var res replbench.Result
	if err := replbench.ReadJSON(path, &res); err != nil {
		log.Fatal(err)
	}
	token := os.Getenv("FLY_API_TOKEN")
	if token == "" {
		log.Fatal("FLY_API_TOKEN is not set")
	}
	var err error
	// Metrics are not part of the correctness verdict: a failure to read them is noted, not counted as an error.
	if res.Resources, err = replbench.FlyResources(context.Background(), res.TerraBaseOrg, res.TerraBaseApp, token, res.Start, res.End); err != nil {
		res.Resources = replbench.Resources{Source: "Fly Prometheus API", Note: "machine metrics could not be read: " + err.Error()}
	}
	out, err := res.Write(filepath.Dir(path))
	if err != nil {
		log.Fatal(err)
	}
	logf("report %s", out)
}
