package main

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
	"os/exec"
	"os/signal"
	"strconv"
	"strings"
	"syscall"
	"time"

	"github.com/oklog/ulid/v2"
	"terrascale.tech/ts-benchmark/internal/apibench"
)

var clientBins = map[string]string{
	"go":         "bin/api-bench-go",
	"dotnet-jit": "clients/dotnet/api-bench/out/jit/api-bench",
	"dotnet-aot": "clients/dotnet/api-bench/out/aot/api-bench",
}

func main() {
	ok, err := run()
	if err != nil {
		fmt.Fprintln(os.Stderr, "api-bench:", err)
	}
	if err != nil || !ok {
		os.Exit(1)
	}
}

func run() (bool, error) {
	clusterState := flag.String("cluster-state", "", "stable or changing (required)")
	clients := flag.String("clients", "go,dotnet-jit,dotnet-aot", "clients to run, sequentially")
	var payloads []int
	flag.Func("payload", "restrict to one payload size in bytes (repeatable)", func(v string) error {
		n, err := strconv.Atoi(v)
		payloads = append(payloads, n)
		return err
	})
	specPath := flag.String("spec", "workloads/api-bench.json", "benchmark spec")
	out := flag.String("out", "bench/results", "report directory")
	flag.Parse()

	host, err := os.Hostname()
	if err != nil {
		return false, err
	}
	if host == "mariogk-desktop" {
		return false, errors.New("refusing to run the API benchmark on mariogk-desktop")
	}
	spec, err := apibench.LoadSpec(*specPath)
	if err != nil {
		return false, err
	}
	plan, err := apibench.Plan(spec, payloads, *clusterState)
	if err != nil {
		return false, err
	}
	if len(plan) == 0 {
		return false, errors.New("no sweeps for the selected payloads and cluster state")
	}
	names := strings.Split(*clients, ",")
	for _, n := range names {
		bin, ok := clientBins[n]
		if !ok {
			return false, fmt.Errorf("unknown client %q", n)
		}
		if _, err := os.Stat(bin); err != nil {
			return false, fmt.Errorf("client %s: %w (run make api-bench-build)", n, err)
		}
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	// Interrupts cancel ctx and return normally, so this defer also runs on the signal path.
	creds, cleanup, err := benchCredentials(ctx)
	if err != nil {
		return false, err
	}
	defer cleanup()
	r := apibench.Run{ID: ulid.Make().String(), Host: host, ClusterState: *clusterState, Endpoint: spec.Endpoint, Start: time.Now().UTC()}
	for _, n := range names {
		ss, err := startup(n, clientBins[n])
		if err != nil {
			return false, err
		}
		r.Startup = append(r.Startup, ss)
	}
	if r.Probe, err = apibench.ProbeEndpoint(ctx, spec.Endpoint, 10); err != nil {
		return false, err
	}
	for _, n := range names {
		if ctx.Err() != nil {
			break
		}
		r.Clients = append(r.Clients, runClient(ctx, n, clientBins[n], *specPath, creds, spec, plan))
	}
	r.End = time.Now().UTC()
	md, js, err := apibench.WriteReport(*out, r)
	if err != nil {
		return false, err
	}
	fmt.Println(md)
	fmt.Println(js)
	ok := ctx.Err() == nil
	for _, c := range r.Clients {
		ok = ok && c.Error == "" && c.Done != nil && c.Done.Cleanup
	}
	return ok, nil
}

// startup launches `<bin> startup` five times and records wall time, init_ms and peak RSS.
func startup(name, bin string) (apibench.StartupStat, error) {
	var wall, initMS, rss []float64
	for range 5 {
		start := time.Now()
		cmd := exec.Command(bin, "startup")
		out, err := cmd.Output()
		if err != nil {
			return apibench.StartupStat{}, fmt.Errorf("%s startup: %w", name, err)
		}
		var st apibench.Startup
		if err := json.Unmarshal(out, &st); err != nil {
			return apibench.StartupStat{}, fmt.Errorf("%s startup: %w", name, err)
		}
		wall = append(wall, time.Since(start).Seconds()*1000)
		initMS = append(initMS, st.InitMS)
		rss = append(rss, float64(cmd.ProcessState.SysUsage().(*syscall.Rusage).Maxrss)/1024)
	}
	return apibench.StartupStat{Client: name, WallMS: apibench.NewStat(wall), InitMS: apibench.NewStat(initMS), MaxRSSMB: apibench.NewStat(rss)}, nil
}

// runClient drives one client through ready, the planned sweeps and done. Any
// failure is recorded in the result; cleanup always runs via stdin EOF.
func runClient(ctx context.Context, name, bin, specPath, credsPath string, spec apibench.Spec, plan []apibench.Sweep) apibench.ClientRun {
	cr := apibench.ClientRun{Client: name}
	fail := func(err error) {
		if cr.Error == "" {
			cr.Error = err.Error()
		}
	}
	cmd := exec.Command(bin, "run", "--spec", specPath, "--credentials", credsPath)
	stdin, err := cmd.StdinPipe()
	if err != nil {
		fail(err)
		return cr
	}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		fail(err)
		return cr
	}
	pr, pw := io.Pipe()
	cmd.Stderr = pw
	go prefixLines(name, pr)
	if err := cmd.Start(); err != nil {
		fail(err)
		return cr
	}
	lines := make(chan []byte)
	go func() {
		sc := bufio.NewScanner(stdout)
		for sc.Scan() {
			lines <- append([]byte(nil), sc.Bytes()...)
		}
		close(lines)
	}()
	decode := func(timeout time.Duration, v any) error {
		select {
		case l, ok := <-lines:
			if !ok {
				return errors.New("client exited")
			}
			return json.Unmarshal(l, v)
		case <-time.After(timeout):
			return fmt.Errorf("no output within %s", timeout)
		}
	}
	send := func(st apibench.Step) (apibench.Result, error) {
		var r apibench.Result
		if ctx.Err() != nil {
			return r, errors.New("interrupted")
		}
		line, err := json.Marshal(st)
		if err != nil {
			return r, err
		}
		if _, err := stdin.Write(append(line, '\n')); err != nil {
			return r, err
		}
		err = decode(time.Duration(st.WarmupS+st.StepS+60)*time.Second, &r)
		return r, err
	}

	var rd apibench.Ready
	if err := decode(5*time.Minute, &rd); err != nil {
		fail(err)
	} else {
		cr.Ready = &rd
		for _, sw := range plan {
			rs, err := apibench.RunSweep(sw, spec, send)
			cr.Results = append(cr.Results, rs...)
			if err != nil {
				fail(err)
				break
			}
		}
	}

	stdin.Close()
	var d apibench.Done
	if err := decode(2*time.Minute, &d); err != nil {
		fail(err)
		_ = cmd.Process.Kill()
	} else {
		cr.Done = &d
	}
	if err := cmd.Wait(); err != nil {
		fail(fmt.Errorf("exit: %w", err))
	}
	pw.Close()
	return cr
}

func prefixLines(name string, r io.Reader) {
	sc := bufio.NewScanner(r)
	for sc.Scan() {
		fmt.Fprintf(os.Stderr, "%s: %s\n", name, sc.Text())
	}
	_, _ = io.Copy(io.Discard, r)
}
