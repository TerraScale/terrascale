package main

import (
	"context"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"

	"terrascale.tech/ts-benchmark/internal/benchclient"
)

type host struct {
	targets benchclient.Targets
	stop    func() error
}

func startHost(ctx context.Context, work string) (*host, error) {
	project, err := filepath.Abs(hostProject)
	if err != nil {
		return nil, err
	}
	file := filepath.Join(work, "cluster.json")
	logPath := filepath.Join(work, "host.log")
	log, err := os.Create(logPath)
	if err != nil {
		return nil, err
	}
	defer log.Close()
	cmd := exec.Command("dotnet", "run", "--project", project, "--configuration", "Release", "--no-build", "--",
		"--treenode-filter", "/*/*/LocalBenchmarkHost/*")
	cmd.Env = append(os.Environ(), "TERRABASE_BENCH_TARGETS="+file)
	cmd.Stdout, cmd.Stderr = log, log

	cmd.SysProcAttr = &syscall.SysProcAttr{Setpgid: true}
	if err := cmd.Start(); err != nil {
		return nil, err
	}
	exited := make(chan error, 1)
	go func() { exited <- cmd.Wait() }()
	h := &host{stop: sync.OnceValue(func() error {
		os.Remove(file)
		select {
		case err := <-exited:
			return err
		case <-time.After(time.Minute):
			_ = syscall.Kill(-cmd.Process.Pid, syscall.SIGKILL)
			<-exited
			return fmt.Errorf("the cluster host did not exit within a minute of its targets file's removal; killed")
		}
	})}
	fail := func(err error) (*host, error) {
		h.stop()
		return nil, fmt.Errorf("%w\n%s", err, tail(logPath))
	}
	for deadline := time.Now().Add(3 * time.Minute); ; {
		if _, err := os.Stat(file); err == nil {
			break
		}
		select {
		case <-ctx.Done():
			return fail(ctx.Err())
		case err := <-exited:
			exited <- err
			return fail(fmt.Errorf("the cluster host exited before it was ready: %v", err))
		case <-time.After(time.Second):
		}
		if time.Now().After(deadline) {
			return fail(fmt.Errorf("the cluster host was not ready within 3 minutes"))
		}
	}
	t, err := benchclient.LoadTargets(file)
	if err != nil {
		return fail(err)
	}
	_, gru := t.Regions[region]
	if !gru || len(t.Regions) != 1 || t.Kind != "local" || t.PID <= 0 {
		return fail(fmt.Errorf("targets: want a local host with regions %s and its pid", hostRegions))
	}
	h.targets = t
	return h, nil
}

func tail(path string) string {
	b, _ := os.ReadFile(path)
	return string(b[max(0, len(b)-4096):])
}

func metricsURL(ctx context.Context, pid int) (string, error) {
	urls, ports, err := metricsListeners(ctx, pid)
	if err != nil {
		return "", err
	}
	c := &http.Client{Timeout: 2 * time.Second}
	var matches []string
	for _, u := range urls {
		if body, err := get(ctx, c, u); err == nil {
			if streams, ok := streamCount(body); ok && streams == 1 {
				matches = append(matches, u)
			}
		}
	}
	if len(matches) == 1 {
		return matches[0], nil
	}
	return "", fmt.Errorf("want exactly one scoped metrics listener of process %d with one calibration stream, found %v (ports %v)", pid, matches, ports)
}

func metricsListeners(ctx context.Context, pid int) ([]string, []int, error) {
	fds, err := os.ReadDir(fmt.Sprintf("/proc/%d/fd", pid))
	if err != nil {
		return nil, nil, err
	}
	inodes := map[string]bool{}
	for _, fd := range fds {
		link, _ := os.Readlink(fmt.Sprintf("/proc/%d/fd/%s", pid, fd.Name()))
		if s, ok := strings.CutPrefix(link, "socket:["); ok {
			inodes[strings.TrimSuffix(s, "]")] = true
		}
	}
	var ports []int
	for _, f := range []string{"tcp", "tcp6"} {
		b, err := os.ReadFile(fmt.Sprintf("/proc/%d/net/%s", pid, f))
		if err != nil {
			return nil, nil, err
		}
		ports = append(ports, listenPorts(string(b), inodes)...)
	}
	slices.Sort(ports)
	ports = slices.Compact(ports)
	c := &http.Client{Timeout: 2 * time.Second}
	var urls []string
	for _, p := range ports {
		u := fmt.Sprintf("http://127.0.0.1:%d/metrics", p)
		if body, err := get(ctx, c, u); err == nil && strings.Contains(body, "terrabase_stats_operations") {
			urls = append(urls, u)
		}
		if err := ctx.Err(); err != nil {
			return nil, nil, fmt.Errorf("metrics discovery: %w", err)
		}
	}
	return urls, ports, nil
}

func streamCount(body string) (float64, bool) {
	lines := promLines(body, "terrabase_changes_streams")
	if len(lines) != 1 {
		return 0, false
	}
	return promValue(lines[0], "terrabase_changes_streams", "")
}

func listenPorts(table string, inodes map[string]bool) []int {
	var ports []int
	for line := range strings.Lines(table) {
		f := strings.Fields(line)
		if len(f) < 10 || f[3] != "0A" || !inodes[f[9]] {
			continue
		}
		_, hex, _ := strings.Cut(f[1], ":")
		if p, err := strconv.ParseUint(hex, 16, 16); err == nil {
			ports = append(ports, int(p))
		}
	}
	return ports
}

func get(ctx context.Context, c *http.Client, u string) (string, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, u, nil)
	if err != nil {
		return "", err
	}
	resp, err := c.Do(req)
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()
	b, err := io.ReadAll(resp.Body)
	if err != nil {
		return "", err
	}
	if resp.StatusCode != http.StatusOK {
		return "", fmt.Errorf("GET %s: HTTP %d", u, resp.StatusCode)
	}
	return string(b), nil
}

func promValue(body, base, label string) (float64, bool) {
	for _, line := range promLines(body, base) {
		if !strings.Contains(line, label) {
			continue
		}
		rest := line[strings.IndexAny(line, "{ "):]
		if strings.HasPrefix(rest, "{") {
			rest = rest[strings.LastIndex(rest, "}")+1:]
		}
		if f := strings.Fields(rest); len(f) > 0 {
			if v, err := strconv.ParseFloat(f[0], 64); err == nil {
				return v, true
			}
		}
	}
	return 0, false
}

const storageReadsMetric = "terrabase_storage_reads"

var storageReadPaths = []string{"point", "query", "feed", "shape", "shape_body"}

var pathLabel = regexp.MustCompile(`[{,]path="([^"]*)"`)

func storageReads(body string) (map[string]float64, error) {
	out := map[string]float64{}
	for _, line := range promLines(body, storageReadsMetric) {
		m := pathLabel.FindStringSubmatch(line)
		if m == nil || !slices.Contains(storageReadPaths, m[1]) {
			return nil, fmt.Errorf("metrics: %s sample without a bounded path label: %q", storageReadsMetric, line)
		}
		if _, dup := out[m[1]]; dup {
			return nil, fmt.Errorf("metrics: two %s series with path %q", storageReadsMetric, m[1])
		}
		v, ok := promValue(line, storageReadsMetric, `path="`+m[1]+`"`)
		if !ok {
			return nil, fmt.Errorf("metrics: unreadable %s sample: %q", storageReadsMetric, line)
		}
		out[m[1]] = v
	}
	return out, nil
}

func pathCounts(body string, paths []string) (map[string]float64, error) {
	counts, err := storageReads(body)
	if err != nil {
		return nil, err
	}
	out := map[string]float64{}
	for _, p := range paths {
		out[p] = counts[p]
	}
	return out, nil
}

func pathDeltas(before map[string]float64, after string, paths []string) (map[string]int64, error) {
	counts, err := storageReads(after)
	if err != nil {
		return nil, err
	}
	out := map[string]int64{}
	for _, p := range paths {
		v, ok := counts[p]
		if !ok {
			return nil, fmt.Errorf("metrics: no %s{path=%q} after the step: no %s SELECT has run on this node, or the path is not instrumented", storageReadsMetric, p, p)
		}
		if v < before[p] {
			return nil, fmt.Errorf("%s{path=%q} fell from %v to %v during the step: the counter reset or the node restarted", storageReadsMetric, p, before[p], v)
		}
		out[p] = int64(v - before[p])
	}
	return out, nil
}

func rawSamples(body string) string {
	var lines []string
	for _, base := range []string{"terrabase_stats_operations", storageReadsMetric, "terrabase_shape_reads", "terrabase_shape_requests_waiting", "terrabase_shape_flights_active"} {
		lines = append(lines, promLines(body, base)...)
	}
	return strings.Join(lines, "\n") + "\n"
}

func promLines(body, base string) []string {
	var out []string
	for line := range strings.Lines(body) {
		i := strings.IndexAny(line, "{ ")
		if i < 0 || strings.HasPrefix(line, "#") {
			continue
		}
		if name := line[:i]; name == base || name == base+"_total" {
			out = append(out, strings.TrimRight(line, "\r\n"))
		}
	}
	return out
}
