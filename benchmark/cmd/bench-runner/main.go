// bench-runner runs one or more protocol clients, then merges their HDR output.
package main

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"github.com/oklog/ulid/v2"
	"os"
	"os/exec"
	"os/signal"
	"path/filepath"
	"strings"
	"syscall"
	"terrascale.tech/ts-benchmark/internal/benchmark"
	"time"
)

type paths []string

const executionBudget = 870 * time.Second

func (p *paths) String() string { return strings.Join(*p, ",") }
func (p *paths) Set(value string) error {
	if value == "" {
		return errors.New("empty path")
	}
	*p = append(*p, value)
	return nil
}
func main() {
	if err := run(os.Args[1:]); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
func run(args []string) error {
	var clients, inputs paths
	f := flag.NewFlagSet("bench-runner", flag.ContinueOnError)
	f.Var(&clients, "client", "client executable (repeatable)")
	f.Var(&inputs, "input", "existing client JSONL (repeatable; aggregate only)")
	workload := f.String("workload", "", "workload JSON")
	region := f.String("region", "", "gru or sfo")
	root := f.String("out-dir", ".", "repository/report output root")
	store := f.Bool("store", true, "persist in TerraBase; false produces explicitly unstored evidence")
	if err := f.Parse(args); err != nil {
		return err
	}
	if f.NArg() != 0 || (*region != "gru" && *region != "sfo") || len(clients)+len(inputs) == 0 || len(clients) > 3 || (len(clients) > 0 && len(inputs) > 0) {
		return errors.New("use --workload, --region and either --client or --input")
	}
	if len(clients) > 0 {
		host, err := os.Hostname()
		if err != nil {
			return err
		}
		if strings.Split(host, ".")[0] == "mariogk-desktop" {
			return errors.New("load generation on mariogk-desktop is forbidden")
		}
	}
	w, err := benchmark.Load(*workload)
	if err != nil {
		return err
	}
	minimum := time.Duration(len(clients)*(w.DurationS+w.WarmupS)) * time.Second
	if minimum > executionBudget {
		return fmt.Errorf("sequential clients require at least %s of measurement/warmup; shared execution budget is %s", minimum, executionBudget)
	}
	signalCtx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	// One shared 15-minute bound includes client setup/load/cleanup and storage.
	ctx, cancel := context.WithTimeout(signalCtx, executionBudget)
	defer cancel()
	id := ulid.Make().String()
	runDir := filepath.Join(*root, "bench", *region, id)
	if err = os.MkdirAll(runDir, 0755); err != nil {
		return err
	}
	clientErrors := map[int]string{}
	for i, path := range clients {
		out := filepath.Join(runDir, fmt.Sprintf("client-%d.jsonl", i))
		inputs = append(inputs, out)
		cmd := exec.CommandContext(ctx, path, "run", "--workload", *workload, "--region", *region, "--out", out)
		// Children own signal cleanup. On timeout give it 30s to revoke/delete.
		cmd.Cancel = func() error { return cmd.Process.Signal(syscall.SIGTERM) }
		cmd.WaitDelay = 30 * time.Second
		if e := cmd.Run(); e != nil {
			clientErrors[i] = fmt.Sprintf("client %d exited unsuccessfully", i)
		}
	}
	report, _, err := benchmark.Aggregate(inputs, w, *region)
	if err != nil {
		receipt := map[string]any{"id": id, "region": *region, "workload": w.Name, "status": "failed", "reason": "client output unavailable or invalid", "client_errors": clientErrors, "cluster_metrics": "unavailable", "stored": false}
		b, e := json.MarshalIndent(receipt, "", "  ")
		if e != nil {
			return e
		}
		if e = os.WriteFile(filepath.Join(runDir, "failure.json"), append(b, '\n'), 0600); e != nil {
			return e
		}
		if e = os.WriteFile(filepath.Join(runDir, "summary.md"), []byte(fmt.Sprintf("# %s / %s\n\n| Status | Throughput | Latency | Cluster metrics | TerraBase stored |\n| --- | --- | --- | --- | --- |\n| FAILED: client output unavailable or invalid | unavailable | unavailable | unavailable | false |\n\nNo capacity verdict: no complete measured run.\n", *region, w.Name)), 0644); e != nil {
			return e
		}
		return fmt.Errorf("no valid measurement; failure receipt: %s", runDir)
	}
	report.ID = id
	clientFailed := false
	if len(clients) > 0 {
		report.ClientExecution = map[string]string{}
		for i, s := range report.Runs {
			status := "success"
			if clientErrors[i] != "" {
				status = "failed"
				if w.Ramp != nil && s.Stopped == "ramp safety threshold exceeded" {
					status = "safety-stop"
				} else {
					report.Verdict = "FAIL"
					clientFailed = true
				}
			}
			report.ClientExecution[s.RunID] = status
		}
	}
	var storageErr error
	if *store {
		storageErr = errors.New("TerraBase persistence unavailable: official ts-go JWT data-client support required")
	}
	b, err := json.MarshalIndent(report, "", "  ")
	if err != nil {
		return err
	}
	if err = os.WriteFile(filepath.Join(runDir, "report.json"), append(b, '\n'), 0600); err != nil {
		return err
	}
	md := report.Markdown()
	if err = os.WriteFile(filepath.Join(runDir, "summary.md"), []byte(md), 0644); err != nil {
		return err
	}
	reportDir := filepath.Join(*root, "reports", *region)
	if err = os.MkdirAll(reportDir, 0755); err != nil {
		return err
	}
	// The report ULID distinguishes repeated aggregation of the same inputs.
	name := report.Start.UTC().Format("2006-01-02T150405.000000000Z") + "-" + w.Name + "-" + report.ID + ".md"
	file, err := os.OpenFile(filepath.Join(reportDir, name), os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0644)
	if err != nil {
		return err
	}
	_, err = file.WriteString(md)
	err = errors.Join(err, file.Close())
	if err != nil {
		return err
	}
	fmt.Printf("%s: %s, %.2f ops/s, %.3f%% errors; %s\n", w.Name, report.Verdict, report.OpsPerS, report.ErrorRate*100, filepath.Join(reportDir, name))
	if storageErr != nil {
		return fmt.Errorf("%w; local evidence retained with stored=false", storageErr)
	}
	if clientFailed {
		return errors.New("client failed after measurement; evidence retained")
	}
	if report.Verdict != "PASS" {
		return errors.New("capacity target failed; evidence retained")
	}
	return nil
}
