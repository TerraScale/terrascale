package main

import (
	"cmp"
	"context"
	"encoding/json/v2"
	"errors"
	"flag"
	"fmt"
	"os"
	"os/exec"
	"os/signal"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
	"syscall"
)

type config struct {
	terrabaseURL string
	authURL      string
	dashboardURL string
	apiKey       string
	project      string
	terrabaseBin string
	authDLL      string
	dashboardDLL string
	csharpBin    string
	rustBin      string
	runners      []string
	iterations   int
	warmup       int
	batchSize    int
	outPath      string
}

func main() {
	if err := run(); err != nil {
		fmt.Fprintf(os.Stderr, "ts-bench: %v\n", err)
		os.Exit(1)
	}
}

func run() error {
	var cfg config
	var runnersRaw string
	flag.StringVar(&cfg.terrabaseURL, "terrabase-url", "", "External TerraBase HTTP URL")
	flag.StringVar(&cfg.authURL, "auth-url", "", "External ts-auth HTTP URL")
	flag.StringVar(&cfg.dashboardURL, "dashboard-url", "", "External TerraScale.Dashboard HTTP URL")
	flag.StringVar(&cfg.apiKey, "api-key", "", "External TerraScale API key")
	flag.StringVar(&cfg.project, "project", "", "External TerraScale project ULID")
	flag.StringVar(&cfg.terrabaseBin, "terrabase-bin", "", "Path to TerraBase binary or dll")
	flag.StringVar(&cfg.authDLL, "auth-dll", "", "Path to ts-auth binary or dll")
	flag.StringVar(&cfg.dashboardDLL, "dashboard-dll", "", "Path to TerraScale.Dashboard binary or dll")
	flag.StringVar(&cfg.csharpBin, "csharp-bin", "", "Path to C# benchmark runner binary or dll")
	flag.StringVar(&cfg.rustBin, "rust-bin", "", "Path to Rust benchmark runner binary")
	flag.StringVar(&runnersRaw, "runners", "csharp,go,rust", "Comma-separated runners: csharp,go,rust")
	flag.IntVar(&cfg.iterations, "iterations", 50, "Measured iterations per scenario")
	flag.IntVar(&cfg.warmup, "warmup", 5, "Warmup iterations per scenario")
	flag.IntVar(&cfg.batchSize, "batch-size", 25, "Statements per sql_rest_batch request")
	flag.StringVar(&cfg.outPath, "out", "", "Optional path to write Markdown report")
	flag.Parse()

	if cfg.iterations < 1 {
		return errors.New("iterations must be >= 1")
	}
	if cfg.warmup < 0 {
		return errors.New("warmup must be >= 0")
	}
	if cfg.batchSize < 1 || cfg.batchSize > 256 {
		return errors.New("batch-size must be between 1 and 256")
	}

	runners, err := parseRunners(runnersRaw)
	if err != nil {
		return err
	}
	cfg.runners = runners

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	external := cfg.terrabaseURL != "" || cfg.authURL != "" || cfg.dashboardURL != "" || cfg.apiKey != "" || cfg.project != ""
	var target stackTarget
	var mode string
	var stack *localStack
	if external {
		if cfg.terrabaseURL == "" || cfg.authURL == "" || cfg.dashboardURL == "" || cfg.apiKey == "" || cfg.project == "" {
			return errors.New("external mode requires --terrabase-url, --auth-url, --dashboard-url, --api-key, and --project")
		}
		target = stackTarget{
			terrabaseURL: strings.TrimRight(cfg.terrabaseURL, "/"),
			authURL:      strings.TrimRight(cfg.authURL, "/"),
			dashboardURL: strings.TrimRight(cfg.dashboardURL, "/"),
			apiKey:       cfg.apiKey,
			project:      cfg.project,
		}
		mode = "external"
	} else {
		var err error
		stack, err = startLocalStack(ctx, cfg)
		if err != nil {
			return err
		}
		defer stack.close()
		target = stack.target
		mode = "self-hosted"
	}

	var reports []sdkReport
	for _, runner := range cfg.runners {
		fmt.Fprintf(os.Stderr, "ts-bench: running %s SDK suite (%d warmup, %d measured, batch %d)...\n", runner, cfg.warmup, cfg.iterations, cfg.batchSize)
		var rep sdkReport
		var runErr error
		switch runner {
		case "go":
			rep, runErr = runGoSuite(ctx, target, cfg.iterations, cfg.warmup, cfg.batchSize)
		case "csharp":
			bin, err := resolveRunnerPath(cfg.csharpBin, []string{
				"csharp/bin/Release/net11.0/TerraScale.Benchmark",
				"csharp/bin/Release/net11.0/TerraScale.Benchmark.dll",
				"csharp/bin/Debug/net11.0/TerraScale.Benchmark",
				"csharp/bin/Debug/net11.0/TerraScale.Benchmark.dll",
			})
			if err != nil {
				return fmt.Errorf("csharp runner: %w", err)
			}
			rep, runErr = runExternalRunner(ctx, bin, target, cfg.iterations, cfg.warmup, cfg.batchSize)
		case "rust":
			bin, err := resolveRunnerPath(cfg.rustBin, []string{
				"rust/target/release/ts-bench-rust",
				"rust/target/debug/ts-bench-rust",
			})
			if err != nil {
				return fmt.Errorf("rust runner: %w", err)
			}
			rep, runErr = runExternalRunner(ctx, bin, target, cfg.iterations, cfg.warmup, cfg.batchSize)
		default:
			return fmt.Errorf("unknown runner %q", runner)
		}
		if runErr != nil {
			return fmt.Errorf("%s runner failed: %w", runner, runErr)
		}
		reports = append(reports, rep)
	}

	var telemetry *resourceTelemetry
	if stack != nil {
		captured, err := stack.captureTelemetry()
		if err != nil {
			return fmt.Errorf("capture terrabase telemetry: %w", err)
		}
		telemetry = &captured
	}

	markdown := renderMarkdown(mode, cfg, reports, telemetry)
	fmt.Print(markdown)
	if cfg.outPath != "" {
		if err := os.MkdirAll(filepath.Dir(cfg.outPath), 0o755); err != nil {
			return fmt.Errorf("create report directory: %w", err)
		}
		if err := os.WriteFile(cfg.outPath, []byte(markdown), 0o644); err != nil {
			return fmt.Errorf("write report %s: %w", cfg.outPath, err)
		}
		fmt.Fprintf(os.Stderr, "ts-bench: saved report to %s\n", cfg.outPath)
	}
	return nil
}

func parseRunners(raw string) ([]string, error) {
	allowed := []string{"csharp", "go", "rust"}
	var out []string
	for part := range strings.SplitSeq(raw, ",") {
		name := strings.ToLower(strings.TrimSpace(part))
		if name == "" {
			continue
		}
		if !slices.Contains(allowed, name) {
			return nil, fmt.Errorf("unsupported runner %q (expected csharp, go, rust)", name)
		}
		if !slices.Contains(out, name) {
			out = append(out, name)
		}
	}
	if len(out) == 0 {
		return nil, errors.New("at least one runner must be selected")
	}
	return out, nil
}

func resolveRunnerPath(explicit string, candidates []string) (string, error) {
	if explicit != "" {
		if _, err := os.Stat(explicit); err != nil {
			return "", err
		}
		return explicit, nil
	}
	baseDir := benchmarkRoot()
	for _, rel := range candidates {
		full := filepath.Join(baseDir, rel)
		if info, err := os.Stat(full); err == nil && !info.IsDir() {
			return full, nil
		}
	}
	return "", fmt.Errorf("binary not found in %s (run make -C benchmark build)", baseDir)
}

func benchmarkRoot() string {
	wd, err := os.Getwd()
	if err == nil {
		if _, statErr := os.Stat(filepath.Join(wd, "go.mod")); statErr == nil {
			return wd
		}
		candidate := filepath.Join(wd, "benchmark")
		if _, statErr := os.Stat(filepath.Join(candidate, "go.mod")); statErr == nil {
			return candidate
		}
	}
	exe, err := os.Executable()
	if err == nil {
		dir := filepath.Dir(filepath.Dir(exe))
		if _, statErr := os.Stat(filepath.Join(dir, "go.mod")); statErr == nil {
			return dir
		}
	}
	return cmp.Or(wd, ".")
}

func runExternalRunner(ctx context.Context, binary string, target stackTarget, iterations, warmup, batchSize int) (sdkReport, error) {
	args := []string{
		"--terrabase-url", target.terrabaseURL,
		"--auth-url", target.authURL,
		"--dashboard-url", target.dashboardURL,
		"--api-key", target.apiKey,
		"--project", target.project,
		"--iterations", strconv.Itoa(iterations),
		"--warmup", strconv.Itoa(warmup),
		"--batch-size", strconv.Itoa(batchSize),
	}
	var cmd *exec.Cmd
	if strings.HasSuffix(binary, ".dll") {
		cmd = exec.CommandContext(ctx, "dotnet", append([]string{binary}, args...)...)
	} else {
		cmd = exec.CommandContext(ctx, binary, args...)
	}
	cmd.Stderr = os.Stderr
	out, err := cmd.Output()
	if err != nil {
		return sdkReport{}, err
	}
	var rep sdkReport
	if err := json.Unmarshal(out, &rep); err != nil {
		return sdkReport{}, fmt.Errorf("decode runner json: %w", err)
	}
	return rep, nil
}
