package replbench

import (
	"fmt"
	"io/fs"
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
	MemMB     Series  `json:"mem_mb"`    // process RSS
	SwapMB    Series  `json:"swap_mb"`
	DiskMB    float64 `json:"disk_mb"`             // data on the volume (local: the node's data directory) at the end
	StealPct  Series  `json:"steal_pct,omitzero"`  // share of CPU time stolen by the host
	Throttled float64 `json:"throttled,omitempty"` // CPU throttling at the end
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
