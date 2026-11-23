package main

import (
	"context"
	"fmt"
	"io"
	"net/http"
	"slices"
	"strings"
	"time"

	"github.com/oklog/ulid/v2"
	"terrascale.tech/sdk/tsauth"
	v1 "terrascale.tech/sdk/v1"
)

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
	slices.Sort(samples)
	var sum float64
	for _, s := range samples {
		sum += s
	}
	ops := 0.0
	if totalElapsed > 0 {
		ops = float64(iterations) / totalElapsed
	}
	return scenarioResult{
		Name:       name,
		Iterations: iterations,
		OpsPerSec:  ops,
		P50Ms:      percentile(samples, 0.50),
		P95Ms:      percentile(samples, 0.95),
		P99Ms:      percentile(samples, 0.99),
		MeanMs:     sum / float64(len(samples)),
		MinMs:      samples[0],
		MaxMs:      samples[len(samples)-1],
	}, nil
}

func percentile(sorted []float64, p float64) float64 {
	if len(sorted) == 0 {
		return 0
	}
	idx := min(max(int(float64(len(sorted)-1)*p+0.5), 0), len(sorted)-1)
	return sorted[idx]
}

func runGoSuite(ctx context.Context, target stackTarget, iterations, warmup, batchSize int) (sdkReport, error) {
	httpClient := &http.Client{
		Timeout: 30 * time.Second,
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
	if _, err := tbClient.CreateDatabase(ctx, target.project, workDBName, v1.CreateDatabaseOptions{}); err != nil {
		return sdkReport{}, fmt.Errorf("create working database: %w", err)
	}
	defer func() {
		_ = tbClient.DeleteDatabase(context.WithoutCancel(ctx), target.project, workDBName)
	}()

	db := tbClient.Database(target.project, workDBName)
	if _, err := db.Execute(ctx, "CREATE TABLE bench_items (id INTEGER PRIMARY KEY, tag TEXT NOT NULL, amount INTEGER NOT NULL)"); err != nil {
		return sdkReport{}, fmt.Errorf("create bench_items table: %w", err)
	}

	nextID := int64(1)
	res, err = measureScenario("sql_rest_write", warmup, iterations, func(_ int) error {
		id := nextID
		nextID++
		_, err := db.Execute(ctx, "INSERT INTO bench_items (id, tag, amount) VALUES (?, ?, ?)", id, "rest", id*10)
		return err
	})
	if err != nil {
		return sdkReport{}, err
	}
	results = append(results, res)

	readMaxID := max(nextID-1, int64(1))
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

	res, err = measureScenario("sql_rest_batch", warmup, iterations, func(_ int) error {
		stmts := make([]v1.Statement, batchSize)
		for i := range batchSize {
			id := nextID
			nextID++
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
		id := nextID
		nextID++
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
