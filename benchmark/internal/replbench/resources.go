package replbench

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"io/fs"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
	"sync"
	"time"
)

// Series is avg, p95 and max of one sampled quantity.
type Series struct {
	Avg float64 `json:"avg"`
	P95 float64 `json:"p95"`
	Max float64 `json:"max"`
	N   int     `json:"samples"`
}

func seriesOf(xs []float64) Series {
	if len(xs) == 0 {
		return Series{}
	}
	s := slices.Clone(xs)
	slices.Sort(s)
	sum := 0.0
	for _, x := range s {
		sum += x
	}
	return Series{Avg: sum / float64(len(s)), P95: s[min(len(s)-1, len(s)*95/100)], Max: s[len(s)-1], N: len(s)}
}

// NodeResources is what one node (local: the host process, shared by every node) used during the load.
type NodeResources struct {
	CPUCores  Series  `json:"cpu_cores"` // cores busy (1.0 = one core fully used)
	MemMB     Series  `json:"mem_mb"`    // local: process RSS; Fly: machine memory in use (total - available)
	SwapMB    Series  `json:"swap_mb"`
	DiskMB    float64 `json:"disk_mb"`             // data on the volume (local: the node's data directory) at the end
	StealPct  Series  `json:"steal_pct,omitzero"`  // Fly: share of CPU time stolen by the host
	Throttled float64 `json:"throttled,omitempty"` // Fly: fly_instance_cpu_throttle at the end
}

// Resources is the per-node record of a run and where it came from.
type Resources struct {
	Source string                   `json:"source"`
	Nodes  map[string]NodeResources `json:"nodes"`
	Note   string                   `json:"note,omitempty"`
}

// LocalSampler samples the host process of the local cluster from /proc every second and each node's data directory
// every ten seconds. The nodes share one process, so CPU, memory and swap are the sum of every node.
type LocalSampler struct {
	pid  int
	dirs map[string]string
	stop chan struct{}
	done chan struct{}

	mu        sync.Mutex
	cpu, rss  []float64
	swap      []float64
	diskFinal map[string]float64
}

func StartLocalSampler(pid int, dirs map[string]string) *LocalSampler {
	s := &LocalSampler{pid: pid, dirs: dirs, stop: make(chan struct{}), done: make(chan struct{}), diskFinal: map[string]float64{}}
	go s.run()
	return s
}

const clockTicks = 100 // USER_HZ on Linux

func (s *LocalSampler) run() {
	defer close(s.done)
	tick := time.NewTicker(time.Second)
	defer tick.Stop()
	lastCPU, lastAt := procCPU(s.pid), time.Now()
	for n := 1; ; n++ {
		select {
		case <-s.stop:
			s.sampleDisk()
			return
		case <-tick.C:
		}
		cpu, at := procCPU(s.pid), time.Now()
		rss, swap := procMem(s.pid)
		s.mu.Lock()
		s.cpu = append(s.cpu, (cpu-lastCPU)/clockTicks/at.Sub(lastAt).Seconds())
		s.rss, s.swap = append(s.rss, rss), append(s.swap, swap)
		s.mu.Unlock()
		lastCPU, lastAt = cpu, at
		if n%10 == 0 {
			s.sampleDisk()
		}
	}
}

func (s *LocalSampler) sampleDisk() {
	for name, dir := range s.dirs {
		var total int64
		_ = filepath.WalkDir(dir, func(_ string, d fs.DirEntry, err error) error {
			if err == nil && !d.IsDir() {
				if info, err := d.Info(); err == nil {
					total += info.Size()
				}
			}
			return nil
		})
		s.mu.Lock()
		s.diskFinal[name] = float64(total) / (1 << 20)
		s.mu.Unlock()
	}
}

// Stop ends sampling and answers the record: one "process" node with CPU and memory, plus each node's disk.
func (s *LocalSampler) Stop() Resources {
	close(s.stop)
	<-s.done
	s.mu.Lock()
	defer s.mu.Unlock()
	out := Resources{Source: fmt.Sprintf("/proc/%d and the data directories", s.pid), Nodes: map[string]NodeResources{
		"all nodes (one process)": {CPUCores: seriesOf(s.cpu), MemMB: seriesOf(s.rss), SwapMB: seriesOf(s.swap)},
	}, Note: "the local nodes run in one host process, so CPU, memory and swap are their sum; disk is per node"}
	for name, mb := range s.diskFinal {
		out.Nodes[name] = NodeResources{DiskMB: mb}
	}
	return out
}

// procCPU is utime + stime of pid in clock ticks.
func procCPU(pid int) float64 {
	b, err := os.ReadFile(fmt.Sprintf("/proc/%d/stat", pid))
	if err != nil {
		return 0
	}
	// The command name may hold spaces; fields restart after its closing parenthesis.
	f := strings.Fields(string(b[strings.LastIndexByte(string(b), ')')+1:]))
	if len(f) < 13 {
		return 0
	}
	u, _ := strconv.ParseFloat(f[11], 64)
	st, _ := strconv.ParseFloat(f[12], 64)
	return u + st
}

// procMem is VmRSS and VmSwap of pid in MiB.
func procMem(pid int) (rss, swap float64) {
	b, err := os.ReadFile(fmt.Sprintf("/proc/%d/status", pid))
	if err != nil {
		return 0, 0
	}
	for line := range strings.Lines(string(b)) {
		f := strings.Fields(line)
		if len(f) < 2 {
			continue
		}
		kb, _ := strconv.ParseFloat(f[1], 64)
		switch f[0] {
		case "VmRSS:":
			rss = kb / 1024
		case "VmSwap:":
			swap = kb / 1024
		}
	}
	return rss, swap
}

// FlyResources reads every machine of app from Fly's Prometheus API over [start, end], one point per 15 s. token is a
// flyctl token ("fm2_…"); it is sent as "FlyV1 <token>" and never logged.
func FlyResources(ctx context.Context, org, app, token string, start, end time.Time) (Resources, error) {
	sel := `app="` + app + `"`
	queries := map[string]string{
		"cpu":    `sum by (instance,region) (rate(fly_instance_cpu{` + sel + `,mode!~"idle|steal|guest|guest_nice"}[30s])) / sum by (instance,region) (rate(fly_instance_cpu{` + sel + `}[30s])) * count by (instance,region) (fly_instance_cpu{` + sel + `,mode="idle"})`,
		"steal":  `100 * sum by (instance,region) (rate(fly_instance_cpu{` + sel + `,mode="steal"}[30s])) / sum by (instance,region) (rate(fly_instance_cpu{` + sel + `}[30s]))`,
		"mem":    `(fly_instance_memory_mem_total{` + sel + `} - fly_instance_memory_mem_available{` + sel + `}) / 1048576`,
		"swap":   `(fly_instance_memory_swap_total{` + sel + `} - fly_instance_memory_swap_free{` + sel + `}) / 1048576`,
		"disk":   `(fly_instance_filesystem_blocks{` + sel + `,mount="/data"} - fly_instance_filesystem_blocks_free{` + sel + `,mount="/data"}) * fly_instance_filesystem_block_size{` + sel + `,mount="/data"} / 1048576`,
		"thrott": `fly_instance_cpu_throttle{` + sel + `}`,
	}
	series := map[string]map[string][]float64{} // query -> "region/instance" -> values
	for name, q := range queries {
		got, err := promRange(ctx, org, token, q, start, end)
		if err != nil {
			return Resources{}, fmt.Errorf("%s: %w", name, err)
		}
		series[name] = got
	}
	out := Resources{Source: "Fly Prometheus API (api.fly.io/prometheus/" + org + "), 15 s steps", Nodes: map[string]NodeResources{}}
	for node := range series["mem"] {
		last := func(xs []float64) float64 {
			if len(xs) == 0 {
				return 0
			}
			return xs[len(xs)-1]
		}
		out.Nodes[node] = NodeResources{
			CPUCores:  seriesOf(series["cpu"][node]),
			MemMB:     seriesOf(series["mem"][node]),
			SwapMB:    seriesOf(series["swap"][node]),
			DiskMB:    last(series["disk"][node]),
			StealPct:  seriesOf(series["steal"][node]),
			Throttled: last(series["thrott"][node]),
		}
	}
	return out, nil
}

func promRange(ctx context.Context, org, token, query string, start, end time.Time) (map[string][]float64, error) {
	q := url.Values{"query": {query}, "start": {strconv.FormatInt(start.Unix(), 10)}, "end": {strconv.FormatInt(end.Unix(), 10)}, "step": {"15"}}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, "https://api.fly.io/prometheus/"+url.PathEscape(org)+"/api/v1/query_range?"+q.Encode(), nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("Authorization", "FlyV1 "+strings.TrimPrefix(token, "FlyV1 "))
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	b, _ := io.ReadAll(resp.Body)
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("HTTP %d: %.200s", resp.StatusCode, b)
	}
	var body struct {
		Data struct {
			Result []struct {
				Metric map[string]string `json:"metric"`
				Values [][2]any          `json:"values"`
			} `json:"result"`
		} `json:"data"`
	}
	if err := json.Unmarshal(b, &body); err != nil {
		return nil, err
	}
	out := map[string][]float64{}
	for _, r := range body.Data.Result {
		key := r.Metric["region"] + "/" + r.Metric["instance"]
		for _, v := range r.Values {
			if s, ok := v[1].(string); ok {
				if f, err := strconv.ParseFloat(s, 64); err == nil {
					out[key] = append(out[key], f)
				}
			}
		}
	}
	return out, nil
}
