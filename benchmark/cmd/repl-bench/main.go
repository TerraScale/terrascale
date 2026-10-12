// Command repl-bench writes documents across every region of one TerraBase database, measures write latency and
// throughput per region, replication lag per region pair and the time to full convergence, verifies every region
// document by document, and records CPU, memory, swap and disk per node.
//
//	repl-bench --targets cluster.json --docs 1000000 --out bench
//
// The targets file comes from the local cluster host (make benchmark-repl-local) or, for Fly, from repl-bench fly-targets.
package main

import (
	"context"
	"flag"
	"fmt"
	"log"
	"os"
	"os/exec"
	"os/signal"
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
	)
	flag.Parse()
	logf := log.New(os.Stderr, "repl-bench ", log.LstdFlags|log.Lmsgprefix).Printf
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
		ConflictRounds: *rounds, ProbeEvery: *probeEvery, Converge: *converge, Seed: *seed}
	if *name == "" {
		*name = fmt.Sprintf("repl-%s-%d", t.Kind, *docs)
	}
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()

	host, _ := os.Hostname()
	res := replbench.Result{Name: *name, Kind: t.Kind, Commit: commit(), Host: host, Start: time.Now(), Machines: t.Machines,
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

	var sampler *replbench.LocalSampler
	if t.Kind == "local" && t.PID > 0 {
		sampler = replbench.StartLocalSampler(t.PID, t.DataDirs)
	}
	var loadSecs float64
	var loadEnd time.Time
	if err := r.Setup(ctx); err != nil {
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
	res.End = time.Now()
	switch {
	case sampler != nil:
		res.Resources = sampler.Stop()
	case t.Kind == "fly":
		token := os.Getenv("FLY_API_TOKEN")
		if token == "" {
			fail("resources", fmt.Errorf("FLY_API_TOKEN is not set; machine metrics were not read"))
		} else if res.Resources, err = replbench.FlyResources(context.WithoutCancel(ctx), t.Org, t.App, token, res.Start, res.End); err != nil {
			fail("resources", err)
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
	b, err := exec.Command("git", "rev-parse", "--short=12", "HEAD").Output()
	if err != nil {
		return "unknown"
	}
	return strings.TrimSpace(string(b))
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
