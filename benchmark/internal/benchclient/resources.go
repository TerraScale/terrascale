package benchclient

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

type NodeResources struct {
	CPUCores  Series  `json:"cpu_cores"`
	MemMB     Series  `json:"mem_mb"`
	SwapMB    Series  `json:"swap_mb"`
	DiskMB    float64 `json:"disk_mb"`
	StealPct  Series  `json:"steal_pct,omitzero"`
	Throttled float64 `json:"throttled,omitempty"`
}

type Resources struct {
	Source string                   `json:"source"`
	Nodes  map[string]NodeResources `json:"nodes"`
	Note   string                   `json:"note,omitempty"`
}

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

const clockTicks = 100

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

func procCPU(pid int) float64 {
	b, err := os.ReadFile(fmt.Sprintf("/proc/%d/stat", pid))
	if err != nil {
		return 0
	}

	f := strings.Fields(string(b[strings.LastIndexByte(string(b), ')')+1:]))
	if len(f) < 13 {
		return 0
	}
	u, _ := strconv.ParseFloat(f[11], 64)
	st, _ := strconv.ParseFloat(f[12], 64)
	return u + st
}

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
