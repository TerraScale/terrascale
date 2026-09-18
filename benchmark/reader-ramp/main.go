package main

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"net/url"
	"os"
	"strconv"
	"strings"
	"time"
)

func main() {
	if len(os.Args) < 2 {
		usage()
		os.Exit(2)
	}
	switch os.Args[1] {
	case "run":
		if err := runCommand(os.Args[2:]); err != nil {
			fmt.Fprintln(os.Stderr, "reader-ramp:", err)
			os.Exit(1)
		}
	case "validate":
		if err := validateCommand(os.Args[2:]); err != nil {
			fmt.Fprintln(os.Stderr, "reader-ramp:", err)
			os.Exit(1)
		}
	default:
		usage()
		os.Exit(2)
	}
}

func usage() {
	fmt.Fprintln(os.Stderr, `usage:
  go run . run --endpoint URL --capability TOKEN --telemetry-url URL \
    --telemetry-token TOKEN --injection-url URL --injection-token TOKEN \
    --confirm-live --output result.json
  go run . validate --result result.json

The runner never contacts an endpoint until --confirm-live is supplied. A
real-zone run requires capability, telemetry, and injection credentials.`)
}

func runCommand(args []string) error {
	fs := flag.NewFlagSet("run", flag.ContinueOnError)
	fs.SetOutput(io.Discard)
	endpoint := fs.String("endpoint", os.Getenv("READER_RAMP_ENDPOINT"), "log URL")
	capability := fs.String("capability", os.Getenv("READER_RAMP_CAPABILITY"), "view capability")
	mixedCapability := fs.String("mixed-capability", os.Getenv("READER_RAMP_MIXED_CAPABILITY"), "unauthorized capability for isolation probe")
	telemetryURL := fs.String("telemetry-url", os.Getenv("READER_RAMP_TELEMETRY_URL"), "normalized telemetry endpoint")
	telemetryToken := fs.String("telemetry-token", os.Getenv("READER_RAMP_TELEMETRY_TOKEN"), "telemetry bearer token")
	injectionURL := fs.String("injection-url", os.Getenv("READER_RAMP_INJECTION_URL"), "fault-injection endpoint")
	injectionToken := fs.String("injection-token", os.Getenv("READER_RAMP_INJECTION_TOKEN"), "fault-injection bearer token")
	out := fs.String("output", "reader-ramp-result.json", "result JSON path")
	readers := fs.String("readers", "10000,100000,1000000", "comma-separated concurrency stages")
	duration := fs.Duration("duration", 30*time.Second, "duration of each stage")
	writeRate := fs.Int("write-rate", 16, "declared origin write rate")
	maxBody := fs.Int64("max-body-bytes", 16<<20, "maximum response body size")
	confirm := fs.Bool("confirm-live", false, "confirm that this command may make live requests")
	allowHTTP := fs.Bool("allow-http", false, "allow HTTP endpoints, only for a local stub")
	injections := fs.String("injections", "cache-bypass,eviction,rule-change,capability-expiry", "faults to inject, or none")
	if err := fs.Parse(args); err != nil {
		return err
	}
	if !*confirm {
		return errors.New("refusing to run without --confirm-live")
	}
	stageReaders, err := parseReaders(*readers)
	if err != nil {
		return err
	}
	if *duration <= 0 || *writeRate != 16 {
		return errors.New("duration must be positive and write-rate must be exactly 16")
	}
	if *maxBody <= 0 {
		return errors.New("max-body-bytes must be positive")
	}
	target, err := checkedEndpoint(*endpoint, *allowHTTP)
	if err != nil {
		return err
	}
	checkedTelemetryURL, err := checkedOptionalEndpoint(*telemetryURL, *allowHTTP, "telemetry")
	if err != nil {
		return err
	}
	checkedInjectionURL, err := checkedOptionalEndpoint(*injectionURL, *allowHTTP, "injection")
	if err != nil {
		return err
	}
	if strings.TrimSpace(*capability) == "" {
		return errors.New("refusing to run without --capability or READER_RAMP_CAPABILITY")
	}
	if strings.TrimSpace(*mixedCapability) == "" {
		return errors.New("refusing to run without --mixed-capability or READER_RAMP_MIXED_CAPABILITY")
	}
	if *injections != "none" && strings.TrimSpace(*injectionURL) == "" {
		return errors.New("injections require --injection-url")
	}
	if target.Scheme == "https" && strings.TrimSpace(*telemetryURL) == "" {
		return errors.New("real-zone runs require --telemetry-url")
	}
	if strings.TrimSpace(*telemetryURL) != "" && target.Scheme == "https" && strings.TrimSpace(*telemetryToken) == "" {
		return errors.New("real-zone telemetry requires --telemetry-token")
	}
	if strings.TrimSpace(*injectionURL) != "" && target.Scheme == "https" && strings.TrimSpace(*injectionToken) == "" {
		return errors.New("real-zone injections require --injection-token")
	}

	cfg := runConfig{
		Endpoint: target, Capability: *capability, MixedCapability: *mixedCapability,
		TelemetryURL: checkedTelemetryURL, TelemetryToken: *telemetryToken,
		InjectionURL: checkedInjectionURL, InjectionToken: *injectionToken,
		Readers: stageReaders, Duration: *duration, WriteRate: *writeRate, MaxBodyBytes: *maxBody,
		InjectionNames: parseInjections(*injections),
	}
	result, err := runRamp(context.Background(), cfg)
	if err != nil {
		return err
	}
	if err := writeResult(*out, result); err != nil {
		return err
	}
	fmt.Printf("wrote %s\n", *out)
	return nil
}

func validateCommand(args []string) error {
	fs := flag.NewFlagSet("validate", flag.ContinueOnError)
	fs.SetOutput(io.Discard)
	path := fs.String("result", "reader-ramp-result.json", "result JSON path")
	allowStub := fs.Bool("allow-stub", false, "validate a local stub result, never a production gate")
	if err := fs.Parse(args); err != nil {
		return err
	}
	f, err := os.Open(*path)
	if err != nil {
		return fmt.Errorf("open result: %w", err)
	}
	defer f.Close()
	result, err := LoadResult(f)
	if err != nil {
		return fmt.Errorf("decode result: %w", err)
	}
	if err := validateResult(result, *allowStub); err != nil {
		return err
	}
	fmt.Println("valid X-5 result")
	return nil
}

func parseReaders(raw string) ([]int, error) {
	parts := strings.Split(raw, ",")
	if len(parts) != len(requiredStages) {
		return nil, fmt.Errorf("readers must contain exactly %d stages", len(requiredStages))
	}
	got := make([]int, len(parts))
	for i, part := range parts {
		n, err := strconv.Atoi(strings.TrimSpace(part))
		if err != nil || n <= 0 {
			return nil, fmt.Errorf("invalid reader count %q", part)
		}
		got[i] = n
	}
	return got, nil
}

func parseInjections(raw string) []string {
	if raw == "none" || strings.TrimSpace(raw) == "" {
		return nil
	}
	var names []string
	for _, name := range strings.Split(raw, ",") {
		name = strings.TrimSpace(name)
		if name != "" {
			names = append(names, name)
		}
	}
	return names
}

func checkedEndpoint(raw string, allowHTTP bool) (*url.URL, error) {
	u, err := url.Parse(strings.TrimSpace(raw))
	if err != nil || u.Scheme == "" || u.Host == "" || u.Path == "" {
		return nil, errors.New("endpoint must be an absolute URL containing a path")
	}
	if u.Scheme != "https" && !(allowHTTP && u.Scheme == "http" && isLoopback(u.Hostname())) {
		return nil, errors.New("endpoint must use HTTPS; HTTP is allowed only for a loopback stub with --allow-http")
	}
	if u.User != nil {
		return nil, errors.New("endpoint userinfo is not allowed")
	}
	return u, nil
}

func checkedOptionalEndpoint(raw string, allowHTTP bool, name string) (string, error) {
	if strings.TrimSpace(raw) == "" {
		return "", nil
	}
	u, err := checkedEndpoint(raw, allowHTTP)
	if err != nil {
		return "", fmt.Errorf("%s %w", name, err)
	}
	return u.String(), nil
}

func isLoopback(host string) bool {
	return host == "127.0.0.1" || host == "::1" || host == "localhost"
}

func writeResult(path string, result Result) error {
	f, err := os.OpenFile(path, os.O_WRONLY|os.O_CREATE|os.O_TRUNC, 0o600)
	if err != nil {
		return fmt.Errorf("create result: %w", err)
	}
	defer f.Close()
	encoder := json.NewEncoder(f)
	encoder.SetIndent("", "  ")
	return encoder.Encode(result)
}
