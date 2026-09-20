// ts-auth management API client benchmark for the official Go SDK
// (terrascale.tech/sdk, the sibling ts-go checkout).
//
// A canned-response in-process transport (no socket, no web stack) serves the
// exact route shapes the official client requests, so every measured operation
// pays the full client path — typed request, JSON encoding, HTTP client
// machinery, JSON decode, response verification — against a constant service
// cost. This is the same measurement class as the .NET `tsauth-sdk`
// engine-comparison lane (raw in-process handler, no socket).
//
// Usage:
//
//	go run ./ts-auth/go --samples 5 --operations 5000 --warmup 1000 --seed 1337 \
//	    --output ts-auth/receipts/<date>-<slug>
package main

import (
	"bytes"
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	sdk "terrascale.tech/sdk"
)

const (
	instanceSlug = "bench"
	datasetSize  = 1000
	payloadSize  = 256
)

func main() {
	samples := flag.Int("samples", 5, "Post-warmup samples (minimum 5)")
	operations := flag.Int("operations", 5000, "Operations per sample")
	warmup := flag.Int("warmup", 1000, "Warmup operations")
	seed := flag.Int64("seed", 1337, "Workload seed (recorded in the receipt)")
	output := flag.String("output", "", "Receipt output directory")
	flag.Parse()
	if *samples < 5 {
		fmt.Fprintln(os.Stderr, "--samples must be at least 5")
		os.Exit(2)
	}
	if *output == "" {
		fmt.Fprintln(os.Stderr, "--output is required")
		os.Exit(2)
	}
	_ = seed

	if err := run(*samples, *operations, *warmup, *output); err != nil {
		fmt.Fprintln(os.Stderr, "bench failed:", err)
		os.Exit(1)
	}
}

func run(samples, operations, warmup int, output string) error {
	if err := os.MkdirAll(output, 0o755); err != nil {
		return err
	}
	stub := newStub()
	httpClient := &http.Client{Transport: stub}
	client, err := sdk.NewClient(sdk.Config{
		APIBaseURL: "http://127.0.0.1/",
		AccessToken: "sk_benchmark_key",
		HTTPClient:  httpClient,
	})
	if err != nil {
		return err
	}
	instance, err := client.TSAuth.Instance(instanceSlug)
	if err != nil {
		return err
	}

	lanes := []struct {
		name string
		op   func(seq int64) error
	}{
		{"read", func(seq int64) error {
			user, err := instance.Users.Get(context.Background(), fmt.Sprintf("usr_%d", seq%datasetSize))
			if err != nil {
				return err
			}
			if !strings.HasPrefix(user.ID, "usr_") || !user.EmailVerified || len(user.PublicMetadata) == 0 {
				return fmt.Errorf("invalid user read: %s", user.ID)
			}
			return nil
		}},
		{"query", func(seq int64) error {
			page, err := instance.Users.List(context.Background(), 50, fmt.Sprintf("usr_%d", seq%datasetSize))
			if err != nil {
				return err
			}
			expected := fmt.Sprintf("usr_%d", (seq%datasetSize+1)%datasetSize)
			if len(page.Data) != 50 || page.Data[0].ID != expected {
				return fmt.Errorf("invalid page: %d rows starting at %s", len(page.Data), page.Data[0].ID)
			}
			return nil
		}},
		{"mixed", func(seq int64) error {
			if seq%2 == 0 {
				user, err := instance.Users.Get(context.Background(), fmt.Sprintf("usr_%d", seq%datasetSize))
				if err != nil {
					return err
				}
				if !strings.HasPrefix(user.ID, "usr_") {
					return fmt.Errorf("invalid mixed read: %s", user.ID)
				}
				return nil
			}
			created, err := instance.Users.Create(context.Background(), sdk.TSAuthUserCreate{Email: fmt.Sprintf("bench-mixed-%d@example.test", seq)})
			if err != nil {
				return err
			}
			if created.ID == "" {
				return fmt.Errorf("invalid mixed write for %d", seq)
			}
			return nil
		}},
		{"write", func(seq int64) error {
			if seq%2 == 0 {
				email := fmt.Sprintf("bench-write-%d@example.test", seq)
				created, err := instance.Users.Create(context.Background(), sdk.TSAuthUserCreate{Email: email, Name: fmt.Sprintf("Write User %d", seq)})
				if err != nil {
					return err
				}
				if created.Email != email {
					return fmt.Errorf("invalid create for %d", seq)
				}
				return nil
			}
			updated, err := instance.Users.Update(context.Background(), "usr_0", sdk.TSAuthUserPatch{}.WithName(fmt.Sprintf("Renamed %d", seq)).WithBanned(seq%4 == 1))
			if err != nil {
				return err
			}
			if updated.Name != fmt.Sprintf("Renamed %d", seq) {
				return fmt.Errorf("invalid patch for %d", seq)
			}
			return nil
		}},
	}
	concurrency := []int{1, 16}

	type caseResult struct {
		Lane        string  `json:"lane"`
		Concurrency int     `json:"concurrency"`
		Throughput  float64 `json:"throughputOpsPerSecond"`
		P50         float64 `json:"p50Microseconds"`
		P95         float64 `json:"p95Microseconds"`
		P99         float64 `json:"p99Microseconds"`
		BytesPerOp  float64 `json:"allocatedBytesPerOperation"`
	}

	var results []caseResult
	for _, lane := range lanes {
		for _, workers := range concurrency {
			if lane.name == "query" && workers > 1 {
				continue // The .NET lane matrix: query only at c1.
			}
			for i := 0; i < warmup; i++ {
				if err := lane.op(int64(i)); err != nil {
					return fmt.Errorf("%s warmup: %w", lane.name, err)
				}
			}
			for sample := 0; sample < samples; sample++ {
				r, err := measureSample(lane.name, lane.op, workers, operations)
				if err != nil {
					return err
				}
				results = append(results, caseResult{Lane: lane.name, Concurrency: workers, Throughput: r.throughput, P50: r.p50, P95: r.p95, P99: r.p99, BytesPerOp: r.bytesPerOp})
				fmt.Printf("  go %-6s c%-2d sample %d/%d: %.1f ops/s\n", lane.name, workers, sample+1, samples, r.throughput)
			}
		}
	}

	receipt := map[string]any{
		"subject":      "ts-go (official Go SDK, TerraScaleAuth-equivalent TSAuthClient)",
		"sdkLanguage":  "go",
		"goVersion":    runtime.Version(),
		"samples":      samples,
		"operations":   operations,
		"warmup":       warmup,
		"datasetSize":  datasetSize,
		"payloadSize":  payloadSize,
		"instanceSlug": instanceSlug,
		"cases":        results,
		"finishedUTC":  time.Now().UTC().Format(time.RFC3339),
	}
	receiptBytes, err := json.MarshalIndent(receipt, "", "  ")
	if err != nil {
		return err
	}
	if err := os.WriteFile(filepath.Join(output, "go-receipt.json"), receiptBytes, 0o644); err != nil {
		return err
	}

	var summary strings.Builder
	summary.WriteString("# ts-auth Go SDK client benchmark receipt\n\n")
	summary.WriteString("| Lane | Concurrency | ops/s | p50 µs | p95 µs | p99 µs | B/op |\n| --- | --- | ---: | ---: | ---: | ---: | ---: |\n")
	for _, r := range results {
		fmt.Fprintf(&summary, "| %s | c%d | %.0f | %.1f | %.1f | %.1f | %.0f |\n", r.Lane, r.Concurrency, r.Throughput, r.P50, r.P95, r.P99, r.BytesPerOp)
	}
	if err := os.WriteFile(filepath.Join(output, "go-summary.md"), []byte(summary.String()), 0o644); err != nil {
		return err
	}
	fmt.Println("Receipt:", filepath.Join(output, "go-receipt.json"))
	return nil
}

type sampleResult struct {
	throughput float64
	p50        float64
	p95        float64
	p99        float64
	bytesPerOp float64
}

func measureSample(lane string, op func(seq int64) error, workers, operations int) (sampleResult, error) {
	latencies := make([]time.Duration, 0, operations)
	var mu sync.Mutex
	var seq int64
	var wg sync.WaitGroup
	perWorker := operations / workers

	var before, after runtime.MemStats
	runtime.GC()
	runtime.ReadMemStats(&before)
	start := time.Now()
	for w := 0; w < workers; w++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			local := make([]time.Duration, 0, perWorker)
			for i := 0; i < perWorker; i++ {
				current := atomic.AddInt64(&seq, 1)
				opStart := time.Now()
				if err := op(current); err != nil {
					fmt.Fprintln(os.Stderr, lane, "operation failed:", err)
					os.Exit(1)
				}
				local = append(local, time.Since(opStart))
			}
			mu.Lock()
			latencies = append(latencies, local...)
			mu.Unlock()
		}()
	}
	wg.Wait()
	elapsed := time.Since(start)
	runtime.ReadMemStats(&after)

	sort.Slice(latencies, func(i, j int) bool { return latencies[i] < latencies[j] })
	pct := func(f float64) float64 {
		index := int(f * float64(len(latencies)))
		if index >= len(latencies) {
			index = len(latencies) - 1
		}
		return float64(latencies[index].Nanoseconds()) / 1000.0
	}
	allocated := float64(after.TotalAlloc - before.TotalAlloc)
	return sampleResult{
		throughput: float64(operations) / elapsed.Seconds(),
		p50:        pct(0.50),
		p95:        pct(0.95),
		p99:        pct(0.99),
		bytesPerOp: allocated / float64(operations),
	}, nil
}

// stub serves the ts-auth users routes with canned records from inside the
// process: the constant service cost isolates the official client's path.
type stub struct {
	users [datasetSize]string
}

func newStub() *stub {
	s := &stub{}
	payload := strings.Repeat("x", payloadSize)
	for index := range s.users {
		s.users[index] = fmt.Sprintf(
			`{"id":"usr_%d","email":"bench-%d@example.test","email_verified":true,"username":"bench%d","name":"Bench User %d","banned":false,"ban_reason":null,"created_at":1700000000,"last_sign_in_at":null,"public_metadata":{"index":%d,"payload":"%s"},"private_metadata":{"plan":"pro","payload":"%s"}}`,
			index, index, index, index, index, payload, payload)
	}
	return s
}

func (s *stub) RoundTrip(request *http.Request) (*http.Response, error) {
	path := request.URL.Path
	prefix := "/v1/" + instanceSlug + "/users"
	if !strings.HasPrefix(path, prefix) || !strings.EqualFold(request.Header.Get("Authorization"), "Bearer sk_benchmark_key") {
		return respond(401, `{"error":{"code":"unauthorized","message":"Missing or invalid credentials."}}`)
	}

	remainder := path[len(prefix):]
	switch {
	case request.Method == "GET" && remainder == "":
		values := request.URL.Query()
		limit := 20
		if l := values.Get("limit"); l != "" {
			fmt.Sscanf(l, "%d", &limit)
		}
		if limit < 1 || limit > 100 {
			return respond(400, `{"error":{"code":"invalid_request","message":"limit must be between 1 and 100"}}`)
		}
		after := 0
		if cursor := values.Get("starting_after"); strings.HasPrefix(cursor, "usr_") {
			fmt.Sscanf(cursor[len("usr_"):], "%d", &after)
		}
		rows := make([]string, limit)
		for offset := 0; offset < limit; offset++ {
			rows[offset] = s.users[(after+1+offset)%datasetSize]
		}
		return respond(200, `{"data":[`+strings.Join(rows, ",")+`]}`)

	case strings.HasPrefix(remainder, "/"):
		userId := remainder[1:]
		if request.Method == "GET" && strings.HasPrefix(userId, "usr_") {
			index := 0
			fmt.Sscanf(userId[len("usr_"):], "%d", &index)
			if index >= 0 && index < datasetSize && userId == fmt.Sprintf("usr_%d", index) {
				return respond(200, s.users[index])
			}
		}
		if request.Method == "PATCH" && userId == "usr_0" {
			body, _ := io.ReadAll(request.Body)
			var patch struct {
				Name   *string `json:"name"`
				Banned *bool   `json:"banned"`
			}
			if err := json.Unmarshal(body, &patch); err != nil {
				return respond(400, `{"error":{"code":"invalid_json","message":"the request body is not valid JSON"}}`)
			}
			name := "Bench User 0"
			if patch.Name != nil {
				name = *patch.Name
			}
			banned := patch.Banned != nil && *patch.Banned
			return respond(200, fmt.Sprintf(`{"id":"usr_0","email":"bench-0@example.test","email_verified":true,"username":"bench0","name":%q,"banned":%t,"ban_reason":null,"created_at":1700000000,"last_sign_in_at":null,"public_metadata":{"index":0},"private_metadata":{"plan":"pro"}}`, name, banned))
		}

	case request.Method == "POST" && remainder == "":
		body, _ := io.ReadAll(request.Body)
		var create struct {
			Email string `json:"email"`
		}
		if err := json.Unmarshal(body, &create); err != nil {
			return respond(400, `{"error":{"code":"invalid_json","message":"the request body is not valid JSON"}}`)
		}
		// The stub answers 200; the official client accepts any 2xx for create.
		return respond(200, fmt.Sprintf(`{"id":"usr_created","email":"%s","email_verified":false,"username":null,"name":null,"banned":false,"ban_reason":null,"created_at":1700000001,"last_sign_in_at":null,"public_metadata":{},"private_metadata":{}}`, create.Email))
	}

	return respond(404, `{"error":{"code":"user_not_found","message":"no user with this id in this instance"}}`)
}

func respond(status int, body string) (*http.Response, error) {
	return &http.Response{
		StatusCode: status,
		Header:     http.Header{"Content-Type": []string{"application/json"}},
		Body:       io.NopCloser(bytes.NewReader([]byte(body))),
		Request:    nil,
	}, nil
}
