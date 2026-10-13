package main

import (
	"context"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"

	"terrascale.tech/ts-benchmark/internal/replbench"
)

type host struct {
	targets replbench.Targets
	stop    func() error
}

// startHost runs tests/TerraBase.E2E's BenchmarkClusterHost with two regions (the Release build, as
// benchmark/scripts/repl-local.sh does) and waits for its targets file. stop deletes the file, which ends the host.
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
		"--treenode-filter", "/*/*/BenchmarkClusterHost/*")
	cmd.Env = append(os.Environ(), "TERRABASE_BENCH_CLUSTER_FILE="+file, "TERRABASE_BENCH_REGIONS="+hostRegions)
	cmd.Stdout, cmd.Stderr = log, log
	// Its own process group: an interrupt reaches this process only, and a kill reaches dotnet run's child too.
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
	t, err := replbench.LoadTargets(file)
	if err != nil {
		return fail(err)
	}
	if _, ok := t.Regions[region]; !ok || t.Kind != "local" || t.PID <= 0 {
		return fail(fmt.Errorf("targets: want a local host with region %s and its pid", region))
	}
	h.targets = t
	return h, nil
}

func tail(path string) string {
	b, _ := os.ReadFile(path)
	return string(b[max(0, len(b)-4096):])
}

// metricsURL finds the host's Prometheus scrape. The targets file names only the client URL, so it asks every TCP port
// the host process listens on for /metrics while exactly one calibration stream is open on gru. Only that node's
// scoped scrape has one open stream; port order alone cannot identify a node in this two-region process.
func metricsURL(ctx context.Context, pid int) (string, error) {
	fds, err := os.ReadDir(fmt.Sprintf("/proc/%d/fd", pid))
	if err != nil {
		return "", err
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
			return "", err
		}
		ports = append(ports, listenPorts(string(b), inodes)...)
	}
	slices.Sort(ports)
	ports = slices.Compact(ports)
	c := &http.Client{Timeout: 2 * time.Second}
	for _, p := range ports {
		u := fmt.Sprintf("http://127.0.0.1:%d/metrics", p)
		if body, err := get(ctx, c, u); err == nil && strings.Contains(body, "terrabase_stats_operations") {
			if streams, ok := promValue(body, "terrabase_changes_streams", ""); ok && streams == 1 {
				return u, nil
			}
		}
	}
	return "", fmt.Errorf("no TCP listener of process %d answers /metrics with terrabase_stats_operations and one calibration stream (ports %v)", pid, ports)
}

// listenPorts are the ports of the LISTEN sockets of a /proc/net/tcp{,6} table whose inode is in inodes.
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

// promValue is the value of the first sample of metric base (or base_total, the exporter's counter name) whose line
// holds label.
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

// promLines are the sample lines of metric base or base_total.
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
