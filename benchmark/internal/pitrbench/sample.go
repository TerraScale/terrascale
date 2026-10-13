package pitrbench

import (
	"context"
	"encoding/json"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"
)

// Sample is one line of samples.jsonl: the node's CPU and memory, its data directory, the host, and backup progress
// (lag, pending bytes, uploaded bytes by kind, failures, holes, generations) over time.
type Sample struct {
	Ms           int64              `json:"t"`
	Node         string             `json:"node"`
	CPUPercent   float64            `json:"cpu_pct"`
	RSSBytes     int64              `json:"rss"`
	DataBytes    int64              `json:"data_bytes"`
	FreeBytes    uint64             `json:"free_bytes"`
	Load1        float64            `json:"load1"`
	CgroupMem    int64              `json:"cgroup_mem,omitempty"`
	LagSeconds   float64            `json:"lag_s"`
	PendingBytes int64              `json:"pending"`
	LogBytes     int64              `json:"log_bytes"`
	ShippedMs    int64              `json:"shipped_ms,omitempty"`
	Generation   string             `json:"generation,omitempty"`
	LastError    string             `json:"last_error,omitempty"`
	Metrics      map[string]float64 `json:"metrics,omitempty"`
	// Clock is the node-vs-host clock offset bound: A-7 judges node restore times against host ledger times.
	Clock *NodeClock `json:"clock,omitempty"`
}

// RSSStopBytes is the node memory stop threshold (8 GB): a sampled node above it is stopped (SIGTERM) and the
// sampler fails.
const RSSStopBytes = MonitorMaxRSSBytes

// SampleLoop appends a sample every interval to evidence/samples-<node>.jsonl until ctx ends. A node whose RSS exceeds
// RSSStopBytes is stopped gracefully, recorded in evidence/rss-stop-<label>.json, and the loop fails.
func SampleLoop(ctx context.Context, r *Run, label string, interval time.Duration) error {
	n, err := LoadNode(r)
	if err != nil {
		return err
	}
	c := NewClient(n.InternalPort, r.Owner, 10*time.Second)
	path := filepath.Join(r.Dir, "evidence", "samples-"+label+".jsonl")
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return err
	}
	prevCPU, prevAt := procTicks(n.PID), time.Now()
	var gone, date time.Time
	for ctx.Err() == nil {
		sleepCtx(ctx, interval)
		if n2, err := LoadNode(r); err == nil && n2.PID != n.PID {
			n, prevCPU = n2, procTicks(n2.PID) // restarted
		}
		now := time.Now()
		// A restart records its new pid at once; a node gone for a minute was stopped for good.
		if alive(n.PID) {
			gone = time.Time{}
		} else if gone.IsZero() {
			gone = now
		} else if now.Sub(gone) > time.Minute {
			return nil
		}
		ticks := procTicks(n.PID)
		s := Sample{Ms: now.UnixMilli(), Node: label, RSSBytes: procRSS(n.PID), DataBytes: dirBytes(n.DataDir), Load1: loadavg()}
		s.CPUPercent = float64(ticks-prevCPU) / 100 / now.Sub(prevAt).Seconds() * 100 // USER_HZ is 100 on Linux
		prevCPU, prevAt = ticks, now
		s.FreeBytes, _ = FreeBytes(r.Dir)
		s.CgroupMem = cgroupMemory()
		if f, ok, err := c.BackupStatus(ctx, r.DatabaseID); err == nil && ok {
			s.LagSeconds, s.PendingBytes, s.LogBytes, s.LastError = f.LagSeconds, f.PendingBytes, f.LogBytes, deref(f.LastError)
			if f.LastShippedAt != nil {
				s.ShippedMs = f.LastShippedAt.UnixMilli()
			}
			s.Generation = deref(f.Generation)
		}
		s.Metrics, _ = scrapeBackupMetrics(ctx, n.MetricsPort)
		s.Clock, date, _ = c.Clock(ctx, date)
		if err := appendLine(path, s); err != nil {
			return err
		}
		if s.RSSBytes > RSSStopBytes {
			stopErr := StopNode(r, 2*time.Minute)
			_ = r.Evidence("rss-stop-"+label+".json", map[string]any{"pid": n.PID, "rss": s.RSSBytes, "threshold": RSSStopBytes,
				"ms": s.Ms, "stop_error": fmt.Sprint(stopErr)})
			return fmt.Errorf("node %s pid %d RSS %d over the %d stop threshold: stopped (%v)", label, n.PID, s.RSSBytes, RSSStopBytes, stopErr)
		}
	}
	return nil
}

func procTicks(pid int) int64 {
	b, err := os.ReadFile("/proc/" + strconv.Itoa(pid) + "/stat")
	if err != nil {
		return 0
	}
	s := string(b)
	f := strings.Fields(s[strings.LastIndexByte(s, ')')+2:])
	if len(f) < 13 {
		return 0
	}
	utime, _ := strconv.ParseInt(f[11], 10, 64)
	stime, _ := strconv.ParseInt(f[12], 10, 64)
	return utime + stime
}

func procRSS(pid int) int64 {
	b, err := os.ReadFile("/proc/" + strconv.Itoa(pid) + "/status")
	if err != nil {
		return 0
	}
	for line := range strings.Lines(string(b)) {
		if rest, ok := strings.CutPrefix(line, "VmRSS:"); ok {
			kb, _ := strconv.ParseInt(strings.Fields(rest)[0], 10, 64)
			return kb * 1024
		}
	}
	return 0
}

func dirBytes(dir string) int64 {
	var total int64
	_ = filepath.WalkDir(dir, func(_ string, d fs.DirEntry, err error) error {
		if err == nil && !d.IsDir() {
			if info, err := d.Info(); err == nil {
				total += info.Size()
			}
		}
		return nil
	})
	return total
}

func loadavg() float64 {
	b, _ := os.ReadFile("/proc/loadavg")
	f := strings.Fields(string(b))
	if len(f) == 0 {
		return 0
	}
	v, _ := strconv.ParseFloat(f[0], 64)
	return v
}

// cgroupMemory is this process's cgroup memory.current (the node shares the harness's cgroup).
func cgroupMemory() int64 {
	b, err := os.ReadFile("/proc/self/cgroup")
	if err != nil {
		return 0
	}
	_, path, _ := strings.Cut(strings.TrimSpace(string(b)), "::")
	v, err := os.ReadFile("/sys/fs/cgroup" + path + "/memory.current")
	if err != nil {
		return 0
	}
	n, _ := strconv.ParseInt(strings.TrimSpace(string(v)), 10, 64)
	return n
}

// ShippedRead is one line of evidence/shipped-<label>.jsonl: a LastShippedAt value of the run's database file, first
// seen at Ms.
type ShippedRead struct {
	Ms           int64   `json:"t"`
	ShippedMs    int64   `json:"shipped_ms"`
	Generation   string  `json:"generation,omitempty"`
	PendingBytes int64   `json:"pending"`
	LagSeconds   float64 `json:"lag_s"`
}

// ShippedLoop appends every distinct LastShippedAt value the node reports for the run's database to
// evidence/shipped-<label>.jsonl, polling every interval (at most 250 ms while a bounded cut's phase runs), until ctx
// ends. It is the inclusion bound's history, independent of the bucket a restore reads. A missed value only makes the
// bound older (more lenient on what must be restored), never wrong.
func ShippedLoop(ctx context.Context, r *Run, label string, interval time.Duration) error {
	c, err := r.Client(10 * time.Second)
	if err != nil {
		return err
	}
	path := filepath.Join(r.Dir, "evidence", "shipped-"+label+".jsonl")
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return err
	}
	last := int64(0)
	for ctx.Err() == nil {
		if f, ok, err := c.BackupStatus(ctx, r.DatabaseID); err == nil && ok && shippedMs(f) != 0 && shippedMs(f) != last {
			last = shippedMs(f)
			if err := appendLine(path, ShippedRead{Ms: time.Now().UnixMilli(), ShippedMs: last, Generation: deref(f.Generation),
				PendingBytes: f.PendingBytes, LagSeconds: f.LagSeconds}); err != nil {
				return err
			}
		}
		sleepCtx(ctx, interval)
	}
	return nil
}

// ReadShipped reads the shipped-read values of a ShippedLoop file. Any unparsable line fails: the history is evidence.
func ReadShipped(path string) ([]int64, error) {
	b, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var out []int64
	for line := range strings.Lines(string(b)) {
		var s ShippedRead
		if err := json.Unmarshal([]byte(line), &s); err != nil || s.ShippedMs == 0 {
			return nil, fmt.Errorf("%s: unparsable line %q (%v)", path, strings.TrimSpace(line), err)
		}
		out = append(out, s.ShippedMs)
	}
	if len(out) == 0 {
		return nil, fmt.Errorf("%s holds no shipped reads", path)
	}
	return out, nil
}
