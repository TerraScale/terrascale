// Command terrabase-bench-adapter drives an already-running production
// tbep-production-harness through the public terrabase-go client.
package main

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"
)

func main() {
	config, err := ParseConfig(os.Args[1:])
	if err != nil {
		fmt.Fprintf(os.Stderr, "invalid benchmark configuration: %v\n", err)
		os.Exit(2)
	}

	result, runErr := Run(context.Background(), config)
	if runErr != nil {
		failure := FailureResults{
			Status:      "failed",
			Target:      targetName,
			Workload:    config.Workload,
			Config:      config.Lane,
			Error:       runErr.Error(),
			Environment: environmentFor(config),
			Samples:     []Sample{},
			Limits: []string{
				"failed attempts are retained for audit and cannot be compared",
				"source identity: " + config.SourceIdentity,
			},
		}
		if writeErr := WriteResultExclusive(config.OutputPath, failure); writeErr != nil {
			fmt.Fprintf(os.Stderr, "benchmark failed: %v (failure result could not be retained: %v)\n", runErr, writeErr)
		} else {
			fmt.Fprintf(os.Stderr, "benchmark failed: %v\n", runErr)
		}
		os.Exit(1)
	}
	if err := WriteResultExclusive(config.OutputPath, result); err != nil {
		fmt.Fprintf(os.Stderr, "benchmark result could not be retained: %v\n", err)
		os.Exit(1)
	}
	fmt.Printf("wrote %s\n", config.OutputPath)
}

const (
	targetName = "terrabase"
	maxCount   = 1_000_000
	maxSamples = 100
	maxSlots   = 256
	maxTimeout = 1 * time.Hour
)

// LaneConfig is the shared benchmark configuration shape used by the Rust
// reference driver and the Rust result reader.
type LaneConfig struct {
	SeedDocuments    int     `json:"seed_documents"`
	Operations       int     `json:"operations"`
	Samples          int     `json:"samples"`
	Concurrency      int     `json:"concurrency"`
	WarmupOperations int     `json:"warmup_operations"`
	NoisePercent     float64 `json:"noise_percent"`
}

var defaultLaneConfig = LaneConfig{
	SeedDocuments:    1_000,
	Operations:       10_000,
	Samples:          5,
	Concurrency:      16,
	WarmupOperations: 1_000,
	NoisePercent:     5,
}

// Config contains all operator-supplied facts needed to drive the harness.
// Server, source, and transport identities are required because the harness
// has no version RPC and a benchmark receipt must not infer them.
type Config struct {
	Endpoint          string
	RootCertPath      string
	ClientCertPath    string
	ClientKeyPath     string
	Authority         string
	Principal         string
	Tenant            string
	ServerIdentity    string
	SourceIdentity    string
	TransportIdentity string
	Workload          string
	OutputPath        string
	Timeout           time.Duration
	Lane              LaneConfig
}

func (c Config) Validate() error {
	for name, value := range map[string]string{
		"endpoint": c.Endpoint, "root-cert": c.RootCertPath, "client-cert": c.ClientCertPath,
		"client-key": c.ClientKeyPath, "principal": c.Principal, "tenant": c.Tenant,
		"server-identity": c.ServerIdentity, "source-identity": c.SourceIdentity,
		"transport-identity": c.TransportIdentity, "workload": c.Workload, "output": c.OutputPath,
	} {
		if strings.TrimSpace(value) == "" {
			return fmt.Errorf("%s is required", name)
		}
		if len(value) > 4096 {
			return fmt.Errorf("%s exceeds 4096 bytes", name)
		}
		for _, b := range []byte(value) {
			if b == 0 || b < 0x20 {
				return fmt.Errorf("%s contains a control character", name)
			}
		}
	}
	if _, err := ParseWorkload(c.Workload); err != nil {
		return err
	}
	if c.Timeout <= 0 || c.Timeout > maxTimeout {
		return fmt.Errorf("timeout must be in (0,%s]", maxTimeout)
	}
	if c.Lane.SeedDocuments <= 0 || c.Lane.SeedDocuments > maxCount {
		return fmt.Errorf("seed-documents must be in 1..%d", maxCount)
	}
	if c.Lane.Operations <= 0 || c.Lane.Operations > maxCount {
		return fmt.Errorf("operations must be in 1..%d", maxCount)
	}
	if c.Lane.Samples <= 0 || c.Lane.Samples > maxSamples {
		return fmt.Errorf("samples must be in 1..%d", maxSamples)
	}
	if c.Lane.Concurrency <= 0 || c.Lane.Concurrency > maxSlots {
		return fmt.Errorf("concurrency must be in 1..%d", maxSlots)
	}
	if c.Lane.Operations%c.Lane.Concurrency != 0 {
		return fmt.Errorf("operations must be divisible by concurrency")
	}
	if c.Lane.WarmupOperations < 0 || c.Lane.WarmupOperations > maxCount {
		return fmt.Errorf("warmup must be in 0..%d", maxCount)
	}
	if c.Lane.WarmupOperations%c.Lane.Concurrency != 0 {
		return fmt.Errorf("warmup must be divisible by concurrency")
	}
	if !finite(c.Lane.NoisePercent) || c.Lane.NoisePercent < 0 || c.Lane.NoisePercent > 100 {
		return fmt.Errorf("noise-percent must be finite and in 0..100")
	}
	return nil
}

// ParseConfig parses flags, using the TERRABASE_BENCH_* environment variables
// as defaults. No lifecycle flag exists: this command never starts or stops a
// harness.
func ParseConfig(args []string) (Config, error) {
	fs := flag.NewFlagSet("terrabase-bench-adapter", flag.ContinueOnError)
	fs.SetOutput(os.Stderr)
	value := func(flagName string, envNames ...string) *string {
		return fs.String(flagName, envDefault(envNames...), "")
	}
	c := Config{Lane: defaultLaneConfig, Timeout: 30 * time.Second}
	endpoint := value("endpoint", "TERRABASE_BENCH_ENDPOINT")
	root := value("root-cert", "TERRABASE_BENCH_ROOT_CERT", "TERRABASE_BENCH_ROOT_CERT_PATH")
	cert := value("client-cert", "TERRABASE_BENCH_CLIENT_CERT", "TERRABASE_BENCH_CLIENT_CERT_PATH")
	key := value("client-key", "TERRABASE_BENCH_CLIENT_KEY", "TERRABASE_BENCH_CLIENT_KEY_PATH")
	authority := value("authority", "TERRABASE_BENCH_AUTHORITY")
	principal := value("principal", "TERRABASE_BENCH_PRINCIPAL")
	tenant := value("tenant", "TERRABASE_BENCH_TENANT")
	server := value("server-identity", "TERRABASE_BENCH_SERVER_IDENTITY", "TERRABASE_BENCH_SERVER_ID")
	source := value("source-identity", "TERRABASE_BENCH_SOURCE_IDENTITY", "TERRABASE_BENCH_SOURCE_ID")
	transport := value("transport-identity", "TERRABASE_BENCH_TRANSPORT_IDENTITY", "TERRABASE_BENCH_TRANSPORT")
	workload := value("workload", "TERRABASE_BENCH_WORKLOAD")
	output := value("output", "TERRABASE_BENCH_OUTPUT", "TERRABASE_BENCH_OUTPUT_PATH")
	fs.StringVar(root, "root-cert-path", *root, "")
	fs.StringVar(cert, "client-cert-path", *cert, "")
	fs.StringVar(key, "client-key-path", *key, "")
	fs.StringVar(server, "server-id", *server, "")
	fs.StringVar(source, "source-id", *source, "")
	fs.StringVar(transport, "transport", *transport, "")
	fs.StringVar(output, "out", *output, "")
	fs.DurationVar(&c.Timeout, "timeout", c.Timeout, "")
	fs.IntVar(&c.Lane.SeedDocuments, "seed-documents", c.Lane.SeedDocuments, "")
	fs.IntVar(&c.Lane.Operations, "operations", c.Lane.Operations, "")
	fs.IntVar(&c.Lane.Samples, "samples", c.Lane.Samples, "")
	fs.IntVar(&c.Lane.Concurrency, "concurrency", c.Lane.Concurrency, "")
	fs.IntVar(&c.Lane.WarmupOperations, "warmup", c.Lane.WarmupOperations, "")
	fs.Float64Var(&c.Lane.NoisePercent, "noise-percent", c.Lane.NoisePercent, "")
	if err := fs.Parse(args); err != nil {
		return Config{}, err
	}
	c.Endpoint, c.RootCertPath, c.ClientCertPath, c.ClientKeyPath = *endpoint, *root, *cert, *key
	c.Authority, c.Principal, c.Tenant = *authority, *principal, *tenant
	c.ServerIdentity, c.SourceIdentity, c.TransportIdentity = *server, *source, *transport
	c.Workload, c.OutputPath = *workload, *output
	return c, c.Validate()
}

func envDefault(names ...string) string {
	for _, name := range names {
		if value, ok := os.LookupEnv(name); ok && value != "" {
			return value
		}
	}
	return ""
}

// WriteResultExclusive retains exactly one immutable JSON attempt.
func WriteResultExclusive(path string, result any) error {
	if path == "" {
		return errors.New("output path is empty")
	}
	data, err := json.MarshalIndent(result, "", "  ")
	if err != nil {
		return err
	}
	data = append(data, '\n')
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	temporary, err := os.CreateTemp(filepath.Dir(path), ".terrabase-bench-result-*")
	if err != nil {
		return err
	}
	temporaryPath := temporary.Name()
	defer os.Remove(temporaryPath)
	if err := temporary.Chmod(0o644); err != nil {
		temporary.Close()
		return err
	}
	if _, err := temporary.Write(data); err != nil {
		temporary.Close()
		return err
	}
	if err := temporary.Sync(); err != nil {
		temporary.Close()
		return err
	}
	if err := temporary.Close(); err != nil {
		return err
	}
	if err := os.Link(temporaryPath, path); err != nil {
		return err
	}
	return nil
}
