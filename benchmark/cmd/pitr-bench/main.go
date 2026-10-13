// Command pitr-bench verifies TerraBase continuous backup and point-in-time restore at scale against one pinned
// TerraBase binary run as its own process (benchmark/docs/pitr-100m.md). Every subcommand takes --run, the run
// directory; nothing in it or in this program's output holds a credential.
//
//	pitr-bench init    --run DIR --binary PATH --commit SHA --docs N --load-split N --seed S --target tigris|local-s3-emulator [--env-file F] [--environment Production]
//	pitr-bench node    --run DIR start|stop|kill|status [--node-dir D --env-file F]
//	pitr-bench setup   --run DIR                       tenant, collections, index (enterprise plan)
//	pitr-bench adopt   --run DIR --node-dir D          the same tenant ids on another (isolated) node
//	pitr-bench probe   --run DIR                       server semantics the writer relies on
//	pitr-bench write   --run DIR --phase load-a|load-b|u1|d1|r1 [--writers 16 --rate 0 --max-batches 0 --min-free-gb 150]
//	pitr-bench cut     --run DIR --name C --applied load-a,... [--partial load-b [--at-ms MS] | --unshipped] [--wait 2h]
//	pitr-bench restore --run DIR --cut C --name DB [--at-ms MS | --at-window-latest] [--must-restore] [--node-dir D]   outcome restored, refused:<code> or failed
//	pitr-bench verify  --run DIR --db DB --cut C [--versions exact|logical --segments audit.json [--injected-key K] --shipped shipped.jsonl --compare verify.json --workers 8 --label L --skew-ms 0] [--node-dir D]
//	pitr-bench sample  --run DIR [--label source --interval 5s] [--node-dir D]
//	pitr-bench reconcile --run DIR [--workers 8]       after any node restart: fences read back, source verified exactly
//	pitr-bench shipped --run DIR [--label source --interval 250ms]   every distinct LastShippedAt (bounded cuts' inclusion bound)
//	pitr-bench monitor --run DIR --lab-dll PITRLAB_DLL [--base DIR] [--test-max-stored-bytes N --test-interval D]
//	                                                    G1 spend and resource limits; stops the run's writers and node on a breach
package main

import (
	"cmp"
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"os"
	"os/signal"
	"path/filepath"
	"strings"
	"syscall"
	"time"

	"terrascale.tech/ts-benchmark/internal/pitrbench"
)

func main() {
	if len(os.Args) < 2 {
		fmt.Fprintln(os.Stderr, "usage: pitr-bench init|node|setup|adopt|probe|write|cut|restore|verify|sample --run DIR ...")
		os.Exit(2)
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	if err := run(ctx, os.Args[1], os.Args[2:]); err != nil {
		log.Printf("pitr-bench %s: %v", os.Args[1], err)
		os.Exit(1)
	}
}

func run(ctx context.Context, cmd string, args []string) error {
	fs := flag.NewFlagSet(cmd, flag.ExitOnError)
	dir := fs.String("run", "", "run directory")
	nodeDir := fs.String("node-dir", "", "another node's directory (an isolated restore target)")
	envFile := fs.String("env-file", "", "0600 env file of the node (bucket, master key); never printed")
	switch cmd {
	case "init":
		binary := fs.String("binary", "", "pinned terrabase binary")
		commit := fs.String("commit", "", "commit the binary was built from")
		docs := fs.Int64("docs", 100_000_000, "documents")
		split := fs.Int64("load-split", 0, "end of load-a (default: 40% of docs)")
		seed := fs.Uint64("seed", 0, "seed (required)")
		parts := fs.Int("partitions", 1000, "verification partitions")
		target := fs.String("target", "", "tigris or local-s3-emulator")
		environment := fs.String("environment", "Production", "node environment (Production refuses to start without backup)")
		fs.Parse(args)
		if *dir == "" || *binary == "" || *commit == "" || *seed == 0 || (*target != "tigris" && *target != "local-s3-emulator" && *target != "none") {
			return fmt.Errorf("--run, --binary, --commit, --seed and --target tigris|local-s3-emulator|none are required")
		}
		if _, err := os.Stat(filepath.Join(*dir, "run.json")); err == nil {
			return fmt.Errorf("%s already holds a run", *dir)
		}
		if *split == 0 {
			*split = *docs * 2 / 5
		}
		sum, err := pitrbench.FileSHA256(*binary)
		if err != nil {
			return err
		}
		abs, _ := filepath.Abs(*binary)
		env := ""
		if *envFile != "" {
			env, _ = filepath.Abs(*envFile)
		}
		if err := os.MkdirAll(*dir, 0o700); err != nil {
			return err
		}
		absDir, _ := filepath.Abs(*dir)
		r := &pitrbench.Run{Dir: absDir, ID: filepath.Base(absDir), Seed: *seed, Docs: *docs, LoadSplit: *split, Partitions: *parts,
			Commit: *commit, Binary: abs, BinarySHA256: sum, EnvFile: env, Environment: *environment, Target: *target, Created: time.Now().UTC()}
		return r.Save()
	}
	// Every other command works on an existing run.
	var rest []string
	switch cmd {
	case "node":
		// The verb may come first (node start --run D) or after the flags (node --run D start).
		if len(args) > 0 && !strings.HasPrefix(args[0], "-") {
			rest, args = args[:1], args[1:]
		}
	}
	var (
		phase      = fs.String("phase", "", "write phase")
		writers    = fs.Int("writers", 16, "writer streams")
		rate       = fs.Float64("rate", 0, "documents per second, 0 unthrottled")
		maxBatches = fs.Int64("max-batches", 0, "stop after this many batches")
		minFreeGB  = fs.Uint64("min-free-gb", 150, "stop writing below this much free disk")
		name       = fs.String("name", "", "cut name or restored database name")
		applied    = fs.String("applied", "", "comma-separated complete phases at the cut")
		partial    = fs.String("partial", "", "phase being written during a bounded cut")
		unshipped  = fs.Bool("unshipped", false, "no backup: record a source-only cut that is NOT a PITR cut and can never be restored")
		wait       = fs.Duration("wait", 2*time.Hour, "how long a cut waits for backup to ship")
		cutName    = fs.String("cut", "", "cut")
		atMs       = fs.Int64("at-ms", 0, "restore: at this exact time instead of the cut's; cut: a bounded cut at this time")
		must       = fs.Bool("must-restore", false, "restore: a typed refusal fails (the restore owner's contract requires 201 at this T); default none required")
		atLatest   = fs.Bool("at-window-latest", false, "restore: at the window's latest (the whole second at or before the newest read mark), which must be at or after the cut")
		injected   = fs.String("injected-key", "", "verify: a fault case's injected object key: audit problems on it are expected; any other fails")
		skewMs     = fs.Int64("skew-ms", 0, "verify: A-7 clock allowance between this host and the node (0: one shared host clock)")
		db         = fs.String("db", "", "database to verify")
		versions   = fs.String("versions", "exact", "exact (source) or logical (restore)")
		segments   = fs.String("segments", "", "PitrLab bucket audit JSON: its problems fail verify; its segment and snapshot times check --shipped")
		shippedLog = fs.String("shipped", "", "shipped-read history (pitr-bench shipped): the bounded cut's inclusion bound; needs --segments")
		compare    = fs.String("compare", "", "earlier verification JSON of the same database and cut: every partition's version digest must match")
		workers    = fs.Int("workers", 8, "verification workers")
		label      = fs.String("label", "", "evidence label")
		interval   = fs.Duration("interval", 0, "sampling interval (sample: 5s, shipped: 250ms)")
		grace      = fs.Duration("grace", 30*time.Minute, "graceful stop wait")
		labDLL     = fs.String("lab-dll", "", "monitor: the built PitrLab test assembly (TerraBase.E2E.dll)")
		base       = fs.String("base", "", "monitor: the run base counted against the disk limit (default: the run directory's parent)")
		testStored = fs.Int64("test-max-stored-bytes", 0, "monitor, TEST ONLY: a lower stored-bytes limit (local emulator runs)")
		testEvery  = fs.Duration("test-interval", 0, "monitor, TEST ONLY: a shorter listing interval (local emulator runs)")
	)
	fs.Parse(args)
	if cmd == "node" && rest == nil {
		rest = fs.Args()
	}
	if cmd == "node" && len(rest) == 0 {
		return fmt.Errorf("node start|stop|kill|status")
	}
	if *dir == "" {
		return fmt.Errorf("--run is required")
	}
	r, err := pitrbench.LoadRun(*dir)
	if err != nil {
		return err
	}
	r.NodeDir = *nodeDir
	if *envFile != "" {
		r.EnvFile, _ = filepath.Abs(*envFile)
	}
	switch cmd {
	case "node":
		switch rest[0] {
		case "start":
			n, err := pitrbench.StartNode(ctx, r)
			if err != nil {
				return err
			}
			return printJSON(n)
		case "stop":
			return pitrbench.StopNode(r, *grace)
		case "kill":
			return pitrbench.KillNode(r)
		case "status":
			c, err := r.Client(10 * time.Second)
			if err != nil {
				return err
			}
			f, ok, err := c.BackupStatus(ctx, r.DatabaseID)
			if err != nil {
				return err
			}
			return printJSON(map[string]any{"listed": ok, "file": f})
		}
		return fmt.Errorf("node %s: unknown", rest[0])
	case "setup":
		return pitrbench.Setup(ctx, r)
	case "adopt":
		if *nodeDir == "" {
			return fmt.Errorf("--node-dir is required")
		}
		return pitrbench.Adopt(ctx, r)
	case "probe":
		steps, err := pitrbench.Probe(ctx, r)
		if err != nil {
			return err
		}
		failed := 0
		for _, s := range steps {
			mark := "PASS"
			switch {
			case s.Skipped:
				mark = "SKIP"
			case !s.Passed:
				mark, failed = "FAIL", failed+1
			}
			fmt.Printf("%s  %s  (%s)\n", mark, s.Name, s.Detail)
		}
		if failed > 0 {
			return fmt.Errorf("%d of %d semantics checks failed", failed, len(steps))
		}
		return nil
	case "write":
		stats, err := pitrbench.Write(ctx, r, pitrbench.WriteConfig{Phase: *phase, Writers: *writers, DocsPerSec: *rate,
			MaxBatches: *maxBatches, MinFreeBytes: *minFreeGB << 30})
		_ = r.Evidence(fmt.Sprintf("write-%s-%s.json", *phase, time.Now().UTC().Format("20060102T150405")), stats)
		_ = printJSON(stats)
		return err
	case "cut":
		var phases []string
		if *applied != "" {
			phases = strings.Split(*applied, ",")
		}
		if *atMs != 0 {
			// A bounded cut at an explicit, past time (T and T-1 ms around a mutation's first segment). Exact cuts are
			// quiesced and timed by the node: never at a given time.
			if *partial == "" {
				return fmt.Errorf("--at-ms records only a bounded cut (--partial)")
			}
			cut := pitrbench.Cut{Name: *name, Kind: "bounded", Applied: phases, Partial: *partial, AtMs: *atMs,
				Note: "bounded cut at an explicit time, recorded " + time.Now().UTC().Format(time.RFC3339)}
			if err := r.AddCut(cut); err != nil {
				return err
			}
			return printJSON(cut)
		}
		cut, err := pitrbench.TakeCut(ctx, r, *name, phases, *partial, *unshipped, *wait)
		if err != nil {
			return err
		}
		if *unshipped {
			fmt.Fprintln(os.Stderr, "UNSHIPPED cut: verifies the source only; it is NOT a PITR cut and cannot be restored")
		}
		return printJSON(cut)
	case "restore":
		cut, err := r.Cut(*cutName)
		if err != nil {
			return err
		}
		at := cut.AtMs
		if *atMs != 0 {
			at = *atMs
		}
		c, err := r.Client(24 * time.Hour)
		if err != nil {
			return err
		}
		if *atLatest {
			if at, err = pitrbench.WindowLatest(ctx, r, c); err != nil {
				return err
			}
			// A snapshot's time or the shipper's own progress is no read mark; before the cut, the cut's oracle does not hold.
			if at%1000 != 0 || at < cut.AtMs {
				return fmt.Errorf("the window's latest %d is not a whole second at or after cut %s (%d)", at, cut.Name, cut.AtMs)
			}
		}
		node := "source"
		if *nodeDir != "" {
			node = *nodeDir
		}
		res, err := pitrbench.Restore(ctx, r, c, *cutName, at, *name, node)
		_ = printJSON(res)
		if err == nil && *must && res.Outcome != pitrbench.OutcomeRestored {
			return fmt.Errorf("restore at %d answered %s, but this T must restore (201)", at, res.Outcome)
		}
		return err
	case "verify":
		cfg := pitrbench.VerifyConfig{Database: *db, Cut: *cutName, Workers: *workers, Versions: *versions, Compare: *compare, Label: *label, SkewMs: *skewMs}
		if *segments != "" {
			if cfg.Audit, err = readAudit(*segments, *injected); err != nil {
				return err
			}
		}
		if *shippedLog != "" {
			if cfg.Shipped, err = pitrbench.ReadShipped(*shippedLog); err != nil {
				return err
			}
		}
		out, err := pitrbench.Verify(ctx, r, cfg)
		if err != nil {
			return err
		}
		summary := map[string]any{"passed": out.Passed, "problems": out.Problems, "totals": out.Totals, "global_digest": out.GlobalDigest,
			"expected_global_digest": out.ExpectedGlobal, "version_global_digest": out.VersionGlobal, "markers": out.Markers,
			"acct_index": out.AcctIndex, "bounded": out.Bounded, "a7": out.A7, "shipped": out.Shipped, "compare": out.Compare,
			"seconds": out.Seconds, "docs_per_sec": out.DocsPerSec}
		_ = printJSON(summary)
		if !out.Passed {
			return fmt.Errorf("verification failed: %s", strings.Join(out.Problems, "; "))
		}
		return nil
	case "sample":
		l := *label
		if l == "" {
			l = "source"
		}
		return pitrbench.SampleLoop(ctx, r, l, cmp.Or(*interval, 5*time.Second))
	case "reconcile":
		out, err := pitrbench.Reconcile(ctx, r, *workers)
		if err != nil {
			return err
		}
		_ = printJSON(map[string]any{"passed": out.Passed, "problems": out.Problems, "totals": out.Totals, "bounded": out.Bounded,
			"acct_index": out.AcctIndex, "markers": out.Markers, "seconds": out.Seconds})
		if !out.Passed {
			return fmt.Errorf("reconcile failed: %s", strings.Join(out.Problems, "; "))
		}
		return nil
	case "monitor":
		if *labDLL == "" {
			return fmt.Errorf("--lab-dll is required")
		}
		b := *base
		if b == "" {
			b = filepath.Dir(r.Dir)
		}
		final, err := pitrbench.Monitor(ctx, r, pitrbench.MonitorConfig{LabDLL: *labDLL, Base: b, TestMaxStoredBytes: *testStored, TestInterval: *testEvery})
		_ = printJSON(final)
		return err
	case "shipped":
		l := *label
		if l == "" {
			l = "source"
		}
		return pitrbench.ShippedLoop(ctx, r, l, cmp.Or(*interval, 250*time.Millisecond))
	}
	return fmt.Errorf("unknown command %q", cmd)
}

// readAudit reads a PitrLab bucket audit: its problems (those on the injected key apart), and every segment and snapshot
// time across its generations.
func readAudit(path, injected string) (*pitrbench.Audit, error) {
	b, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var audit struct {
		Verdict  string `json:"verdict"`
		Problems []struct {
			Kind   string  `json:"kind"`
			Key    *string `json:"key"`
			Detail string  `json:"detail"`
		} `json:"problems"`
		Generations []struct {
			Snapshots []struct {
				AtMs int64 `json:"at_ms"`
			} `json:"snapshots"`
			Segments []struct {
				AtMs int64 `json:"at_ms"`
			} `json:"segments"`
		} `json:"generations"`
	}
	if err := json.Unmarshal(b, &audit); err != nil {
		return nil, fmt.Errorf("%s: %w", path, err)
	}
	info, err := os.Stat(path)
	if err != nil {
		return nil, err
	}
	if audit.Verdict == "" {
		return nil, fmt.Errorf("%s is not a bucket audit (no verdict)", path)
	}
	out := &pitrbench.Audit{AtMs: info.ModTime().UnixMilli()}
	for _, p := range audit.Problems {
		key := ""
		if p.Key != nil {
			key = " " + *p.Key
		}
		if injected != "" && p.Key != nil && *p.Key == injected {
			out.Expected = append(out.Expected, p.Kind+key+": "+p.Detail)
			continue
		}
		out.Problems = append(out.Problems, p.Kind+key+": "+p.Detail)
	}
	if audit.Verdict != "ok" && len(audit.Problems) == 0 {
		out.Problems = append(out.Problems, "verdict "+audit.Verdict)
	}
	for _, g := range audit.Generations {
		for _, s := range g.Snapshots {
			out.Times = append(out.Times, s.AtMs)
		}
		for _, s := range g.Segments {
			out.Times = append(out.Times, s.AtMs)
		}
	}
	return out, nil
}

func printJSON(v any) error {
	b, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		return err
	}
	fmt.Println(string(b))
	return nil
}
