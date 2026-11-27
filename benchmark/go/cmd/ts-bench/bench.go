package main

import (
	"bytes"
	"context"
	"encoding/json/v2"
	"errors"
	"fmt"
	"io"
	"net/http"
	"slices"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/oklog/ulid/v2"
	"terrascale.tech/sdk/tsauth"
	v1 "terrascale.tech/sdk/v1"
)

const concurrentWorkers = 16

type scenarioResult struct {
	Name       string  `json:"name"`
	Iterations int     `json:"iterations"`
	OpsPerSec  float64 `json:"opsPerSec"`
	P50Ms      float64 `json:"p50Ms"`
	P95Ms      float64 `json:"p95Ms"`
	P99Ms      float64 `json:"p99Ms"`
	MeanMs     float64 `json:"meanMs"`
	MinMs      float64 `json:"minMs"`
	MaxMs      float64 `json:"maxMs"`
}

type sdkReport struct {
	SDK       string           `json:"sdk"`
	Scenarios []scenarioResult `json:"scenarios"`
}

type rowWritePayload struct {
	Row map[string]any `json:"row"`
}

type rowResponseBody struct {
	Key string         `json:"key"`
	Row map[string]any `json:"row"`
}

func summarizeSamples(name string, samples []float64, totalElapsed float64) scenarioResult {
	slices.Sort(samples)
	var sum float64
	for _, s := range samples {
		sum += s
	}
	ops := 0.0
	if totalElapsed > 0 {
		ops = float64(len(samples)) / totalElapsed
	}
	return scenarioResult{
		Name:       name,
		Iterations: len(samples),
		OpsPerSec:  ops,
		P50Ms:      percentile(samples, 0.50),
		P95Ms:      percentile(samples, 0.95),
		P99Ms:      percentile(samples, 0.99),
		MeanMs:     sum / float64(len(samples)),
		MinMs:      samples[0],
		MaxMs:      samples[len(samples)-1],
	}
}

func measureScenario(name string, warmup, iterations int, fn func(iter int) error) (scenarioResult, error) {
	for i := range warmup {
		if err := fn(-(warmup - i)); err != nil {
			return scenarioResult{}, fmt.Errorf("%s warmup %d: %w", name, i, err)
		}
	}
	samples := make([]float64, iterations)
	totalStart := time.Now()
	for i := range iterations {
		started := time.Now()
		if err := fn(i); err != nil {
			return scenarioResult{}, fmt.Errorf("%s iter %d: %w", name, i, err)
		}
		samples[i] = float64(time.Since(started).Nanoseconds()) / 1e6
	}
	totalElapsed := time.Since(totalStart).Seconds()
	return summarizeSamples(name, samples, totalElapsed), nil
}

func measureScenarioWithCleanup(name string, warmup, iterations int, fn func(iter int) error, cleanup func() error) (scenarioResult, error) {
	for i := range warmup {
		fnErr := fn(-(warmup - i))
		cleanErr := cleanup()
		if fnErr != nil {
			return scenarioResult{}, fmt.Errorf("%s warmup %d: %w", name, i, fnErr)
		}
		if cleanErr != nil {
			return scenarioResult{}, fmt.Errorf("%s warmup %d cleanup: %w", name, i, cleanErr)
		}
	}
	samples := make([]float64, iterations)
	var measured time.Duration
	for i := range iterations {
		started := time.Now()
		fnErr := fn(i)
		elapsed := time.Since(started)
		cleanErr := cleanup()
		if fnErr != nil {
			return scenarioResult{}, fmt.Errorf("%s iter %d: %w", name, i, fnErr)
		}
		if cleanErr != nil {
			return scenarioResult{}, fmt.Errorf("%s iter %d cleanup: %w", name, i, cleanErr)
		}
		measured += elapsed
		samples[i] = float64(elapsed.Nanoseconds()) / 1e6
	}
	return summarizeSamples(name, samples, measured.Seconds()), nil
}

func measureConcurrentScenario(ctx context.Context, name string, concurrency, warmup, iterations int, fn func(ctx context.Context, iter int) error) (scenarioResult, error) {
	if warmup > 0 {
		if err := runConcurrentBatch(ctx, concurrency, warmup, func(ctx context.Context, idx int) error {
			return fn(ctx, -(concurrency*warmup - idx))
		}, nil); err != nil {
			return scenarioResult{}, fmt.Errorf("%s warmup: %w", name, err)
		}
	}
	totalOps := concurrency * iterations
	samples := make([]float64, totalOps)
	totalStart := time.Now()
	if err := runConcurrentBatch(ctx, concurrency, iterations, fn, samples); err != nil {
		return scenarioResult{}, fmt.Errorf("%s: %w", name, err)
	}
	totalElapsed := time.Since(totalStart).Seconds()
	return summarizeSamples(name, samples, totalElapsed), nil
}

func runConcurrentBatch(ctx context.Context, concurrency, perWorker int, fn func(ctx context.Context, iter int) error, samples []float64) error {
	batchCtx, cancel := context.WithCancelCause(ctx)
	defer cancel(nil)
	errs := make([]error, concurrency)
	var wg sync.WaitGroup
	for worker := range concurrency {
		wg.Go(func() {
			base := worker * perWorker
			for j := range perWorker {
				if batchCtx.Err() != nil {
					return
				}
				idx := base + j
				started := time.Now()
				if err := fn(batchCtx, idx); err != nil {
					errs[worker] = err
					cancel(err)
					return
				}
				if samples != nil {
					samples[idx] = float64(time.Since(started).Nanoseconds()) / 1e6
				}
			}
		})
	}
	wg.Wait()
	return errors.Join(errs...)
}

func percentile(sorted []float64, p float64) float64 {
	if len(sorted) == 0 {
		return 0
	}
	idx := min(max(int(float64(len(sorted)-1)*p+0.5), 0), len(sorted)-1)
	return sorted[idx]
}

func runGoSuite(ctx context.Context, target stackTarget, iterations, warmup, batchSize int) (sdkReport, error) {
	transport := &http.Transport{
		MaxIdleConns:        64,
		MaxIdleConnsPerHost: 32,
		IdleConnTimeout:     90 * time.Second,
	}
	defer transport.CloseIdleConnections()

	httpClient := &http.Client{
		Timeout:   30 * time.Second,
		Transport: transport,
		CheckRedirect: func(*http.Request, []*http.Request) error {
			return http.ErrUseLastResponse
		},
	}

	tbClient, err := v1.New(v1.Config{
		BaseURL:       target.terrabaseURL,
		TSAuthBaseURL: target.authURL,
		APIKey:        target.apiKey,
		HTTPClient:    httpClient,
	})
	if err != nil {
		return sdkReport{}, err
	}

	runTag := strings.ToLower(ulid.Make().String()[18:])
	var results []scenarioResult

	res, err := measureScenario("auth_token_exchange", warmup, iterations, func(_ int) error {
		src, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{
			Issuer:     target.authURL + "/terrascale",
			Key:        target.apiKey,
			Project:    target.project,
			HTTPClient: httpClient,
		})
		if err != nil {
			return err
		}
		tok, err := src.Token(ctx)
		if err != nil {
			return err
		}
		if tok == "" {
			return fmt.Errorf("empty access token")
		}
		return nil
	})
	if err != nil {
		return sdkReport{}, err
	}
	results = append(results, res)

	lcCounter := 0
	res, err = measureScenario("db_lifecycle", warmup, iterations, func(_ int) error {
		lcCounter++
		dbName := fmt.Sprintf("b-go-lc-%s-%d", runTag, lcCounter)
		if _, err := tbClient.CreateDatabase(ctx, target.project, dbName, v1.CreateDatabaseOptions{}); err != nil {
			return err
		}
		if _, err := tbClient.Database(target.project, dbName).Execute(ctx, "CREATE TABLE items (id INTEGER PRIMARY KEY, label TEXT NOT NULL, val INTEGER NOT NULL)"); err != nil {
			_ = tbClient.DeleteDatabase(ctx, target.project, dbName)
			return err
		}
		return tbClient.DeleteDatabase(ctx, target.project, dbName)
	})
	if err != nil {
		return sdkReport{}, err
	}
	results = append(results, res)

	workDBName := "b-go-work-" + runTag
	workDB, err := tbClient.CreateDatabase(ctx, target.project, workDBName, v1.CreateDatabaseOptions{})
	if err != nil {
		return sdkReport{}, fmt.Errorf("create working database: %w", err)
	}
	defer func() {
		_ = tbClient.DeleteDatabase(context.WithoutCancel(ctx), target.project, workDBName)
	}()

	db := tbClient.Database(target.project, workDBName)
	if _, err := db.Execute(ctx, "CREATE TABLE bench_items (id INTEGER PRIMARY KEY, tag TEXT NOT NULL, amount INTEGER NOT NULL)"); err != nil {
		return sdkReport{}, fmt.Errorf("create bench_items table: %w", err)
	}

	var nextID atomic.Int64
	res, err = measureScenario("sql_rest_write", warmup, iterations, func(_ int) error {
		id := nextID.Add(1)
		_, err := db.Execute(ctx, "INSERT INTO bench_items (id, tag, amount) VALUES (?, ?, ?)", id, "rest", id*10)
		return err
	})
	if err != nil {
		return sdkReport{}, err
	}
	results = append(results, res)

	readMaxID := max(nextID.Load(), int64(1))
	readCursor := int64(0)
	res, err = measureScenario("sql_rest_read", warmup, iterations, func(_ int) error {
		targetID := (readCursor % readMaxID) + 1
		readCursor++
		rows, err := db.Query(ctx, "SELECT id, tag, amount FROM bench_items WHERE id = ?", targetID)
		if err != nil {
			return err
		}
		if len(rows) != 1 {
			return fmt.Errorf("expected 1 row for id %d, got %d", targetID, len(rows))
		}
		return nil
	})
	if err != nil {
		return sdkReport{}, err
	}
	results = append(results, res)

	var concReadCursor atomic.Int64
	res, err = measureConcurrentScenario(ctx, "sql_rest_concurrent_read", concurrentWorkers, warmup, iterations, func(ctx context.Context, _ int) error {
		targetID := ((concReadCursor.Add(1) - 1) % readMaxID) + 1
		rows, err := db.Query(ctx, "SELECT id, tag, amount FROM bench_items WHERE id = ?", targetID)
		if err != nil {
			return err
		}
		if len(rows) != 1 {
			return fmt.Errorf("expected 1 row for id %d, got %d", targetID, len(rows))
		}
		return nil
	})
	if err != nil {
		return sdkReport{}, err
	}
	results = append(results, res)

	res, err = measureConcurrentScenario(ctx, "sql_rest_concurrent_write", concurrentWorkers, warmup, iterations, func(ctx context.Context, _ int) error {
		id := nextID.Add(1)
		_, err := db.Execute(ctx, "INSERT INTO bench_items (id, tag, amount) VALUES (?, ?, ?)", id, "conc", id*10)
		return err
	})
	if err != nil {
		return sdkReport{}, err
	}
	results = append(results, res)

	res, err = measureScenario("sql_rest_batch", warmup, iterations, func(_ int) error {
		stmts := make([]v1.Statement, batchSize)
		for i := range batchSize {
			id := nextID.Add(1)
			stmts[i] = v1.Statement{
				SQL:  "INSERT INTO bench_items (id, tag, amount) VALUES (?, ?, ?)",
				Args: []any{id, "batch", id * 10},
			}
		}
		out, err := db.Batch(ctx, stmts)
		if err != nil {
			return err
		}
		if len(out) != batchSize {
			return fmt.Errorf("expected %d batch results, got %d", batchSize, len(out))
		}
		return nil
	})
	if err != nil {
		return sdkReport{}, err
	}
	results = append(results, res)

	session, err := db.Connect(ctx)
	if err != nil {
		return sdkReport{}, fmt.Errorf("connect SQL websocket: %w", err)
	}
	defer func() { _ = session.Close() }()

	res, err = measureScenario("sql_ws_transaction", warmup, iterations, func(_ int) error {
		id := nextID.Add(1)
		return session.Transaction(ctx, func(tx *v1.SQLSession) error {
			if _, err := tx.Execute(ctx, "INSERT INTO bench_items (id, tag, amount) VALUES (?, ?, ?)", id, "ws", id*10); err != nil {
				return err
			}
			rows, err := tx.Query(ctx, "SELECT id, tag, amount FROM bench_items WHERE id = ?", id)
			if err != nil {
				return err
			}
			if len(rows) != 1 {
				return fmt.Errorf("expected 1 row in ws tx, got %d", len(rows))
			}
			return nil
		})
	})
	if err != nil {
		return sdkReport{}, err
	}
	results = append(results, res)

	brCounter := 0
	var createdBranch string
	res, err = measureScenarioWithCleanup("db_branch", warmup, iterations, func(_ int) error {
		brCounter++
		branchName := fmt.Sprintf("b-go-br-%s-%d", runTag, brCounter)
		if _, err := db.Branch(ctx, branchName); err != nil {
			return err
		}
		createdBranch = branchName
		rows, err := tbClient.Database(target.project, branchName).Query(ctx, "SELECT id, tag, amount FROM bench_items WHERE id = ?", 1)
		if err != nil {
			return err
		}
		if len(rows) != 1 {
			return fmt.Errorf("expected 1 row in branch %s for id 1, got %d", branchName, len(rows))
		}
		return nil
	}, func() error {
		if createdBranch == "" {
			return nil
		}
		name := createdBranch
		createdBranch = ""
		return tbClient.DeleteDatabase(context.WithoutCancel(ctx), target.project, name)
	})
	if err != nil {
		return sdkReport{}, err
	}
	results = append(results, res)

	tokenSource, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{
		Issuer:     target.authURL + "/terrascale",
		Key:        target.apiKey,
		Project:    target.project,
		HTTPClient: httpClient,
	})
	if err != nil {
		return sdkReport{}, fmt.Errorf("create rows token source: %w", err)
	}
	rowsBaseURL := fmt.Sprintf("%s/v1/projects/%s/databases/%s/tables/bench_items/rows", target.terrabaseURL, target.project, workDB.ID)

	res, err = measureScenario("rows_crud", warmup, iterations, func(_ int) error {
		token, err := tokenSource.Token(ctx)
		if err != nil {
			return err
		}
		id := nextID.Add(1)
		var created rowResponseBody
		if err := doRowRequest(ctx, httpClient, http.MethodPost, rowsBaseURL, token, rowWritePayload{
			Row: map[string]any{"id": id, "tag": "rows-create", "amount": id * 10},
		}, http.StatusCreated, &created); err != nil {
			return err
		}
		if created.Key == "" {
			return errors.New("rows POST returned empty key")
		}
		rowURL := rowsBaseURL + "/" + created.Key

		var fetched rowResponseBody
		if err := doRowRequest(ctx, httpClient, http.MethodGet, rowURL, token, nil, http.StatusOK, &fetched); err != nil {
			return err
		}
		if fetched.Key != created.Key || fetched.Row["tag"] != "rows-create" {
			return fmt.Errorf("rows GET mismatch: key=%q tag=%v", fetched.Key, fetched.Row["tag"])
		}

		var updated rowResponseBody
		if err := doRowRequest(ctx, httpClient, http.MethodPatch, rowURL, token, rowWritePayload{
			Row: map[string]any{"tag": "rows-patch", "amount": id*10 + 1},
		}, http.StatusOK, &updated); err != nil {
			return err
		}
		if updated.Key != created.Key || updated.Row["tag"] != "rows-patch" {
			return fmt.Errorf("rows PATCH mismatch: key=%q tag=%v", updated.Key, updated.Row["tag"])
		}

		return doRowRequest(ctx, httpClient, http.MethodDelete, rowURL, token, nil, http.StatusNoContent, nil)
	})
	if err != nil {
		return sdkReport{}, err
	}
	results = append(results, res)

	res, err = measureScenario("dashboard_roundtrip", warmup, iterations, func(_ int) error {
		if err := getDashboardOK(ctx, httpClient, target.dashboardURL+"/health/live"); err != nil {
			return err
		}
		return getDashboardOK(ctx, httpClient, target.dashboardURL+"/.well-known/oauth-authorization-server")
	})
	if err != nil {
		return sdkReport{}, err
	}
	results = append(results, res)

	return sdkReport{
		SDK:       "go",
		Scenarios: results,
	}, nil
}

func doRowRequest(ctx context.Context, client *http.Client, method, endpoint, token string, payload any, wantStatus int, out any) error {
	var bodyReader io.Reader
	if payload != nil {
		encoded, err := json.Marshal(payload)
		if err != nil {
			return err
		}
		bodyReader = bytes.NewReader(encoded)
	}
	req, err := http.NewRequestWithContext(ctx, method, endpoint, bodyReader)
	if err != nil {
		return err
	}
	req.Header.Set("Authorization", "Bearer "+token)
	if payload != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	resp, err := client.Do(req)
	if err != nil {
		return err
	}
	body, readErr := io.ReadAll(resp.Body)
	_ = resp.Body.Close()
	if readErr != nil {
		return readErr
	}
	if resp.StatusCode != wantStatus {
		return fmt.Errorf("%s %s returned status %d (expected %d): %s", method, endpoint, resp.StatusCode, wantStatus, string(body))
	}
	if out != nil {
		if err := json.Unmarshal(body, out); err != nil {
			return fmt.Errorf("decode %s %s response: %w", method, endpoint, err)
		}
	}
	return nil
}

func getDashboardOK(ctx context.Context, client *http.Client, targetURL string) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, targetURL, nil)
	if err != nil {
		return err
	}
	resp, err := client.Do(req)
	if err != nil {
		return err
	}
	_, _ = io.Copy(io.Discard, resp.Body)
	_ = resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("GET %s returned status %d", targetURL, resp.StatusCode)
	}
	return nil
}
