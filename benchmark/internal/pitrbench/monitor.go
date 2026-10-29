package pitrbench

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"math"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
	"syscall"
	"time"
)

// G1 limits (lead, 2026-10-12: at most US$1 the first month, 5 GB stored, 30 GB local disk, 8 GB node RSS). The monitor
// stops the run below each, with a margin. One setting each; the only override lowers the stored-bytes limit for a test
// against the local emulator.
const (
	MonitorMaxStoredBytes = 4_500_000_000
	MonitorMaxUSD         = 0.80
	MonitorMaxRunBytes    = 28_000_000_000
	MonitorMaxRSSBytes    = 8_000_000_000

	// programCeil* bound the explicit overrides a run may pass (user approval 2026-10-16: a US$25 hard stop for the
	// 1K→100M ladder on host delta; stored and disk ceilings sized for one 100M pass and the host's free space).
	programCeilUSD    = 25.0
	programCeilStored = int64(350) << 30
	programCeilRun    = int64(700) << 30

	// Tigris list prices (2026-10-12); egress is free.
	usdPerGiBMonth = 0.02
	usdPerClassA   = 0.005 / 1000
	usdPerClassB   = 0.0005 / 1000

	// sdkAttempts bounds the requests one failed backup pass can make (the S3 SDK retries each request).
	sdkAttempts = 5
	// minPartBytes is S3's smallest part: the most parts a multipart snapshot of n bytes can have is n/minPartBytes+1.
	minPartBytes = 5 << 20
)

// MonitorConfig is one monitor invocation.
type MonitorConfig struct {
	LabDLL string // the built PitrLab test assembly (inventory mode)
	Base   string // the run base whose size counts against the disk limit (source, copies, targets)
	// Test only: a lower stored-bytes limit and a shorter inventory interval, for a run against the local emulator.
	TestMaxStoredBytes int64
	TestInterval       time.Duration
	// Explicit limits for an approved program that exceeds the G1 constants (each must stay at or below its ceiling).
	MaxUSD         float64
	MaxStoredBytes int64
	MaxRunBytes    int64
}

// MonitorSample is one line of evidence/monitor.jsonl.
type MonitorSample struct {
	Ms            int64              `json:"t"`
	StoredBytes   int64              `json:"stored_bytes"`
	Objects       int64              `json:"objects"`
	MonitorLists  int64              `json:"monitor_list_requests"`
	ClassA        int64              `json:"class_a_estimate"`
	ClassB        int64              `json:"class_b_estimate"`
	USD           float64            `json:"projected_usd_first_month"`
	NodeRSSMax    int64              `json:"node_rss_max"`
	RunBytes      int64              `json:"run_base_bytes"`
	Node          map[string]float64 `json:"node_counters,omitempty"`
	InventoryNote string             `json:"inventory_error,omitempty"`
	Breach        string             `json:"breach,omitempty"`
	TestLimits    bool               `json:"test_limits,omitempty"`
}

// limits are the thresholds one monitor enforces.
type limits struct {
	stored int64
	usd    float64
	run    int64
	rss    int64
}

// breach names the first limit a sample reaches, or "".
func (l limits) breach(s MonitorSample) string {
	switch {
	case s.StoredBytes >= l.stored:
		return fmt.Sprintf("stored bytes %d reached the limit %d", s.StoredBytes, l.stored)
	case s.USD >= l.usd:
		return fmt.Sprintf("projected first-month cost $%.4f reached the limit $%.2f", s.USD, l.usd)
	case s.NodeRSSMax >= l.rss:
		return fmt.Sprintf("node RSS %d reached the limit %d", s.NodeRSSMax, l.rss)
	case s.RunBytes >= l.run:
		return fmt.Sprintf("run base %d bytes reached the limit %d", s.RunBytes, l.run)
	}
	return ""
}

// cost estimates the requests and the first month's price. Class A: every stored object (one PUT each), up to the
// parts the node's uploaded snapshot bytes can make, every request a failed pass can make, the monitor's and the
// harness's LISTs, and the node's drill LISTs. Class B: the harness's GETs (downloads, Path A restores) and the node's
// drill reads (a drill reads about a generation's objects). Storage: the stored bytes kept a whole month. An overestimate
// only stops the run earlier.
func cost(s *MonitorSample, harnessLists, harnessGets int64) {
	n := s.Node
	generations := max(1, n["generations"])
	perGeneration := float64(s.Objects) / generations
	parts := n["uploaded_snapshot_bytes"]/minPartBytes + generations
	s.ClassA = s.Objects + int64(parts) + int64(sdkAttempts*n["failures"]) + s.MonitorLists + harnessLists + int64(n["drills"])
	s.ClassB = harnessGets + int64(n["drills"]*perGeneration)
	s.USD = float64(s.StoredBytes)/(1<<30)*usdPerGiBMonth + float64(s.ClassA)*usdPerClassA + float64(s.ClassB)*usdPerClassB
}

// counters accumulates the node's monotonic backup counters across node restarts (each start resets them).
type counters struct{ last, base map[string]float64 }

func (c *counters) add(now map[string]float64) map[string]float64 {
	if c.last == nil {
		c.last, c.base = map[string]float64{}, map[string]float64{}
	}
	out := map[string]float64{}
	for k, v := range now {
		if v < c.last[k] {
			c.base[k] += c.last[k] // a restart
		}
		c.last[k] = v
	}
	for k := range c.last {
		out[k] = c.base[k] + c.last[k]
	}
	return out
}

// Monitor watches a G1 run until ctx ends or a limit is reached: RSS of the run's node every 10 s, the run base's size
// every minute, and every 10 minutes a read-only listing of the whole (dedicated) bucket through PitrLab's inventory
// mode with the credentials of the run's 0600 env file, the node's backup counters, and the cost estimate. Each listing
// is a line of evidence/monitor.jsonl. On a breach it stops every writer of the run (SIGTERM: in-flight batches stay
// unresolved, nothing is lost) and stops the node gracefully, then fails. It always ends with a final estimate in
// evidence/monitor-final.json.
func Monitor(ctx context.Context, r *Run, cfg MonitorConfig) (MonitorSample, error) {
	lim := limits{stored: MonitorMaxStoredBytes, usd: MonitorMaxUSD, run: MonitorMaxRunBytes, rss: MonitorMaxRSSBytes}
	if cfg.MaxUSD != 0 || cfg.MaxStoredBytes != 0 || cfg.MaxRunBytes != 0 {
		if cfg.MaxUSD != 0 && (cfg.MaxUSD <= 0 || cfg.MaxUSD > programCeilUSD) {
			return MonitorSample{}, fmt.Errorf("a usd limit must be above 0 and at most $%.2f", programCeilUSD)
		}

		if cfg.MaxStoredBytes != 0 && (cfg.MaxStoredBytes <= 0 || cfg.MaxStoredBytes > programCeilStored) {
			return MonitorSample{}, fmt.Errorf("a stored-bytes limit must be above 0 and at most %d", programCeilStored)
		}

		if cfg.MaxRunBytes != 0 && (cfg.MaxRunBytes <= 0 || cfg.MaxRunBytes > programCeilRun) {
			return MonitorSample{}, fmt.Errorf("a run-bytes limit must be above 0 and at most %d", programCeilRun)
		}

		if cfg.TestMaxStoredBytes != 0 || cfg.TestInterval != 0 {
			return MonitorSample{}, fmt.Errorf("test overrides and explicit limits cannot be combined")
		}

		// The ceilings bind together: one limit raised does not raise the others.
		lim.usd = max(cfg.MaxUSD, lim.usd)
		lim.stored = max(cfg.MaxStoredBytes, lim.stored)
		lim.run = max(cfg.MaxRunBytes, lim.run)
	}

	interval := 10 * time.Minute
	test := cfg.TestMaxStoredBytes != 0 || cfg.TestInterval != 0
	if cfg.TestMaxStoredBytes != 0 {
		if cfg.TestMaxStoredBytes < 0 || cfg.TestMaxStoredBytes >= MonitorMaxStoredBytes {
			return MonitorSample{}, fmt.Errorf("a test stored-bytes limit must be below %d", int64(MonitorMaxStoredBytes))
		}
		lim.stored = cfg.TestMaxStoredBytes
	}
	if cfg.TestInterval != 0 {
		if cfg.TestInterval <= 0 || cfg.TestInterval > interval {
			return MonitorSample{}, fmt.Errorf("a test interval must be shorter than %s", interval)
		}
		interval = cfg.TestInterval
	}
	if r.EnvFile == "" {
		return MonitorSample{}, fmt.Errorf("the run has no env file: nothing to list")
	}
	env, err := inventoryEnv(r.EnvFile)
	if err != nil {
		return MonitorSample{}, err
	}
	dir := filepath.Join(r.Dir, "monitor")
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return MonitorSample{}, err
	}
	var (
		acc                counters
		last               MonitorSample
		rssMax, runBytes   int64
		lists              int64
		nextInventory      time.Time
		nextDisk           time.Time
		breach             string
		inventories        int
		harnessLists, gets int64
	)
	sample := func(ctx context.Context) MonitorSample {
		s := MonitorSample{Ms: time.Now().UnixMilli(), NodeRSSMax: rssMax, RunBytes: runBytes, TestLimits: test}
		inv, err := inventory(ctx, cfg.LabDLL, env, dir)
		if err != nil {
			s.InventoryNote = err.Error()
			s.StoredBytes, s.Objects = last.StoredBytes, last.Objects // the last known values
		} else {
			s.StoredBytes, s.Objects = inv.Bytes, inv.Objects
			lists += inv.ListRequests
		}
		s.MonitorLists = lists
		if n, err := LoadNode(r); err == nil {
			if now, err := nodeCounters(ctx, n.MetricsPort); err == nil {
				s.Node = acc.add(now)
			}
		}
		if s.Node == nil {
			s.Node = last.Node
		}
		if s.Node == nil {
			s.Node = map[string]float64{}
		}
		harnessLists, gets = harnessRequests(filepath.Dir(r.Dir), r.Dir, s.Objects)
		cost(&s, harnessLists, gets)
		inventories++
		return s
	}
	tick := time.NewTicker(10 * time.Second)
	defer tick.Stop()
watch:
	for {
		now := time.Now()
		if n, err := LoadNode(r); err == nil && alive(n.PID) {
			rssMax = max(rssMax, procRSS(n.PID))
		}
		if now.After(nextDisk) {
			runBytes, nextDisk = dirBytes(cfg.Base), now.Add(time.Minute)
		}
		if now.After(nextInventory) {
			last, nextInventory = sample(ctx), now.Add(interval)
			log.Printf("monitor: %d objects, %d bytes stored, ~%d class A, ~%d class B, projected $%.4f, node RSS max %d, run base %d",
				last.Objects, last.StoredBytes, last.ClassA, last.ClassB, last.USD, last.NodeRSSMax, last.RunBytes)
			breach = lim.breach(last)
			last.Breach = breach
			if err := appendLine(filepath.Join(r.Dir, "evidence", "monitor.jsonl"), last); err != nil {
				return last, err
			}
		} else {
			// RSS and disk between listings, against the last listing's cost.
			s := last
			s.NodeRSSMax, s.RunBytes = rssMax, runBytes
			breach = lim.breach(s)
		}
		if breach != "" {
			break
		}
		select {
		case <-ctx.Done():
			break watch
		case <-tick.C:
		}
	}
	var stopErr error
	if breach != "" {
		log.Printf("monitor: LIMIT: %s; stopping the run's writers and its node", breach)
		stopped := stopWriters(r)
		stopErr = StopNode(r, 30*time.Minute)
		_ = r.Evidence("monitor-stop.json", map[string]any{"breach": breach, "writers_stopped": stopped, "node_stop_error": fmt.Sprint(stopErr),
			"ms": time.Now().UnixMilli(), "test_limits": test})
	}
	fctx, cancel := context.WithTimeout(context.WithoutCancel(ctx), 10*time.Minute)
	defer cancel()
	final := sample(fctx) // the bucket as left, the final estimate
	final.NodeRSSMax, final.RunBytes, final.Breach = rssMax, runBytes, breach
	_ = r.Evidence("monitor-final.json", map[string]any{"final": final, "inventories": inventories, "limits": map[string]any{
		"stored_bytes": lim.stored, "usd": lim.usd, "run_base_bytes": lim.run, "node_rss_bytes": lim.rss, "test": test},
		"prices": map[string]float64{"usd_per_gib_month": usdPerGiBMonth, "usd_per_class_a": usdPerClassA, "usd_per_class_b": usdPerClassB}})
	if breach != "" {
		return final, errors.Join(fmt.Errorf("limit reached: %s", breach), stopErr)
	}
	return final, nil
}

// inventoryEnv is the env file's bucket settings for PitrLab (no master key: listing opens nothing), plus this
// process's environment without any AWS or TLS-trust setting. Errors name settings, never values.
func inventoryEnv(path string) ([]string, error) {
	return inventoryEnvFrom(path, os.Environ())
}

func inventoryEnvFrom(path string, inherited []string) ([]string, error) {
	vars, err := readEnvFile(path)
	if err != nil {
		return nil, err
	}
	var env []string
	for _, kv := range inherited {
		name, _, _ := strings.Cut(kv, "=")
		if !strings.HasPrefix(name, "AWS_") && !strings.HasPrefix(name, "SSL_CERT_") && !strings.HasPrefix(name, "TERRABASE_") && name != "BUCKET_NAME" {
			env = append(env, kv)
		}
	}
	for _, kv := range vars {
		if name, _, _ := strings.Cut(kv, "="); name != "TERRABASE_MASTER_KEY" {
			env = append(env, kv)
		}
	}
	return env, nil
}

type inventoryReport struct {
	Objects      int64 `json:"objects"`
	Bytes        int64 `json:"bytes"`
	ListRequests int64 `json:"list_requests"`
}

// inventory lists the whole bucket once through PitrLab (read-only: ListObjectsV2 only).
func inventory(ctx context.Context, labDLL string, env []string, dir string) (inventoryReport, error) {
	job, out := filepath.Join(dir, "inventory-job.json"), filepath.Join(dir, "inventory.json")
	_ = os.Remove(out)
	if err := writeJSON(job, map[string]any{"mode": "inventory", "source": map[string]string{"kind": "s3"}, "prefixes": []string{""}, "out": out}); err != nil {
		return inventoryReport{}, err
	}
	cmd := exec.CommandContext(ctx, "nice", "-n", "19", "dotnet", labDLL, "--treenode-filter", "/*/*/PitrLabHost/*", "--output", "detailed")
	cmd.Env = append(env, "TERRABASE_PITR_JOB="+job, "DOTNET_gcServer=0")
	logf, err := os.OpenFile(filepath.Join(dir, "inventory.log"), os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o600)
	if err != nil {
		return inventoryReport{}, err
	}
	defer logf.Close()
	cmd.Stdout, cmd.Stderr = logf, logf
	if err := cmd.Run(); err != nil {
		return inventoryReport{}, fmt.Errorf("PitrLab inventory: %w (see %s)", err, filepath.Join(dir, "inventory.log"))
	}
	var rep inventoryReport
	b, err := os.ReadFile(out)
	if err == nil {
		err = json.Unmarshal(b, &rep)
	}
	return rep, err
}

// nodeCounters scrapes the node's Prometheus endpoint (its metrics port) for the backup counters the estimate uses.
func nodeCounters(ctx context.Context, port int) (map[string]float64, error) {
	m, err := scrapeBackupMetrics(ctx, port)
	if err != nil {
		return nil, err
	}
	out := map[string]float64{}
	for name, v := range m {
		switch {
		case strings.HasPrefix(name, "terrabase_backup_uploaded_bytes_total") && strings.Contains(name, `kind="snapshot"`):
			out["uploaded_snapshot_bytes"] += v
		case strings.HasPrefix(name, "terrabase_backup_uploaded_bytes_total"):
			out["uploaded_segment_bytes"] += v
		case strings.HasPrefix(name, "terrabase_backup_failures_total"):
			out["failures"] += v
		case strings.HasPrefix(name, "terrabase_backup_generations_total"):
			out["generations"] += v
		case strings.HasPrefix(name, "terrabase_backup_drills_total"):
			out["drills"] += v
		}
	}
	return out, nil
}

// scrapeBackupMetrics reads the terrabase_backup* samples (name{labels} -> value) from the node's metrics port, which
// serves Prometheus over plain HTTP/1.1 (the internal listener does not carry these samples).
func scrapeBackupMetrics(ctx context.Context, port int) (map[string]float64, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, "http://127.0.0.1:"+strconv.Itoa(port)+"/metrics", nil)
	if err != nil {
		return nil, err
	}
	resp, err := (&http.Client{Timeout: 10 * time.Second}).Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("/metrics: HTTP %d", resp.StatusCode)
	}
	out := map[string]float64{}
	s := bufio.NewScanner(io.LimitReader(resp.Body, 64<<20))
	s.Buffer(make([]byte, 64<<10), 1<<20)
	for s.Scan() {
		line := s.Text()
		if !strings.HasPrefix(line, "terrabase_backup") {
			continue
		}
		i := strings.LastIndexByte(line, ' ')
		if i < 0 {
			continue
		}
		if v, err := strconv.ParseFloat(line[i+1:], 64); err == nil && !math.IsNaN(v) {
			out[line[:i]] = v
		}
	}
	return out, s.Err()
}

// harnessRequests estimates the harness's own requests to the bucket from its evidence: each byte-exact download lists
// its prefixes and GETs every object (out/download-*-manifest.json), and each Path A restore on the source node reads
// about every object (evidence/restore-*.json of the source node).
func harnessRequests(base, runDir string, objects int64) (lists, gets int64) {
	manifests, _ := filepath.Glob(filepath.Join(base, "out", "download-*-manifest.json"))
	for _, m := range manifests {
		var entries []json.RawMessage
		if b, err := os.ReadFile(m); err == nil && json.Unmarshal(b, &entries) == nil {
			gets += int64(len(entries))
			lists += 2 + int64(len(entries))/1000
		}
	}
	restores, _ := filepath.Glob(filepath.Join(runDir, "evidence", "restore-*.json"))
	for _, f := range restores {
		var res RestoreResult
		if b, err := os.ReadFile(f); err == nil && json.Unmarshal(b, &res) == nil && res.Node == "source" {
			gets += objects
			lists += 1 + objects/1000
		}
	}
	return lists, gets
}

func sameDir(a, b string) bool {
	x, err1 := filepath.Abs(a)
	y, err2 := filepath.Abs(b)
	return err1 == nil && err2 == nil && x == y
}

// stopWriters sends SIGTERM to every pitr-bench write of this run (its in-flight batches stay unresolved in the ledger,
// to be proven by the next write) and waits up to a minute for them to exit. It returns their pids.
func stopWriters(r *Run) []int {
	var pids []int
	entries, _ := os.ReadDir("/proc")
	for _, e := range entries {
		pid, err := strconv.Atoi(e.Name())
		if err != nil || pid == os.Getpid() {
			continue
		}
		b, err := os.ReadFile("/proc/" + e.Name() + "/cmdline")
		if err != nil {
			continue
		}
		args := strings.Split(strings.TrimRight(string(b), "\x00"), "\x00")
		if len(args) > 2 && filepath.Base(args[0]) == "pitr-bench" && args[1] == "write" && slices.ContainsFunc(args, func(a string) bool { return sameDir(a, r.Dir) }) {
			if syscall.Kill(pid, syscall.SIGTERM) == nil {
				pids = append(pids, pid)
			}
		}
	}
	for deadline := time.Now().Add(time.Minute); time.Now().Before(deadline) && slices.ContainsFunc(pids, alive); {
		time.Sleep(200 * time.Millisecond)
	}
	return pids
}
