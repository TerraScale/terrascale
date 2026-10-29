package main

import (
	"context"
	"encoding/json/v2"
	"flag"
	"fmt"
	"log"
	"math"
	"os"
	"os/exec"
	"runtime"
	"slices"
	"strconv"
	"strings"
	"time"

	"terrascale.tech/ts-benchmark/internal/benchclient"
)

const groups = 129
const queries = groups - 1

type item struct {
	ID       string   `json:"id"`
	Score    *float64 `json:"score"`
	Document struct {
		N int `json:"n"`
	} `json:"document"`
}

type page struct {
	Items []item `json:"items"`
	Count struct {
		Value string `json:"value"`
		Exact bool   `json:"exact"`
	} `json:"count"`
}

type result struct {
	size, repeat, bytes int
	build, elapsed      time.Duration
	latencies           []float64
}

func main() {
	targets := flag.String("targets", "", "private local cluster targets file")
	sizes := flag.String("sizes", "10000,100000", "comma-separated document counts (258–129000)")
	out := flag.String("out", "bench/2026-10-23-fts.md", "Markdown report")
	flag.Parse()
	if err := run(*targets, *sizes, *out); err != nil {
		log.Fatal(err)
	}
}

func run(targets, sizes, out string) error {
	t, err := benchclient.LoadTargets(targets)
	if err != nil {
		return err
	}

	if t.Kind != "local" || t.Regions["gru"].URL == "" {
		return fmt.Errorf("requires the local test cluster with gru")
	}
	r, err := benchclient.NewRegion("gru", t, 1)
	if err != nil {
		return err
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Hour)
	defer cancel()
	var results []result
	for raw := range strings.SplitSeq(sizes, ",") {
		size, err := strconv.Atoi(strings.TrimSpace(raw))
		if err != nil || size < 2*groups || size > 1000*groups {
			return fmt.Errorf("sizes must contain integers from %d to %d", 2*groups, 1000*groups)
		}
		for repeat := 1; repeat <= 3; repeat++ {
			row, err := measure(ctx, r, size, repeat)
			if err != nil {
				return fmt.Errorf("documents=%d repeat=%d: %w", size, repeat, err)
			}
			results = append(results, row)
			if err := os.WriteFile(out, []byte(report(results)), 0o644); err != nil {
				return err
			}
			log.Printf("documents=%d repeat=%d: index %.1f ms; %d correct ranked queries", size, repeat, float64(row.build)/float64(time.Millisecond), queries)
		}
	}
	return nil
}

func term(group int) string {
	return "topic" + string([]byte{byte('a' + group/26), byte('a' + group%26)})
}

func document(n int) map[string]any {
	frequency := 1
	if n/groups%2 == 0 {
		frequency = 4
	}
	return map[string]any{"n": n, "article": map[string]any{
		"body": strings.Repeat(term(n%groups)+" ", frequency) + strings.TrimSpace(strings.Repeat("orchard ", 32-frequency)),
	}}
}

func query(group, limit int, count bool) map[string]any {
	return map[string]any{
		"filter": map[string]any{"search": []any{"/article/body", term(group)}},
		"sort":   []any{map[string]any{"path": "$score", "order": "desc"}},
		"limit":  limit, "count": count,
	}
}

func measure(ctx context.Context, r *benchclient.Region, size, repeat int) (result, error) {
	row := result{size: size, repeat: repeat}
	collection := "fts" + strconv.FormatInt(time.Now().UnixNano(), 36)
	if _, err := call(ctx, r, "POST", "/collections", map[string]any{"name": collection}); err != nil {
		return row, err
	}
	for start := 0; start < size; start += 250 {
		var ops []any
		for n := start; n < min(start+250, size); n++ {
			doc := document(n)
			encoded, err := json.Marshal(doc)
			if err != nil {
				return row, err
			}
			row.bytes += len(encoded)
			ops = append(ops, map[string]any{"op": "put", "collection": collection, "id": fmt.Sprintf("%026d", n+1), "document": doc})
		}
		if _, err := call(ctx, r, "POST", "/transactions", map[string]any{"ops": ops}); err != nil {
			return row, fmt.Errorf("seed documents %d–%d: %w", start, min(start+250, size)-1, err)
		}
	}
	started := time.Now()
	body, err := call(ctx, r, "POST", "/indexes", map[string]any{
		"name": collection + "_text", "collection": collection, "kind": "text", "fields": []string{"article.body"},
	})
	row.build = time.Since(started)
	if err != nil {
		return row, err
	}
	var index struct {
		Kind  string `json:"kind"`
		State string `json:"state"`
	}
	if err := json.Unmarshal(body, &index); err != nil {
		return row, err
	}
	if index.Kind != "text" || index.State != "active" {
		return row, fmt.Errorf("index response must confirm kind=text and state=active")
	}
	listed, err := call(ctx, r, "GET", "/indexes?collection="+collection, nil)
	if err != nil {
		return row, err
	}
	var indexes struct {
		Items []struct {
			Kind string `json:"kind"`
		} `json:"items"`
	}
	if err := json.Unmarshal(listed, &indexes); err != nil {
		return row, err
	}
	if len(indexes.Items) != 1 || indexes.Items[0].Kind != "text" {
		return row, fmt.Errorf("index list must confirm one text index")
	}
	path := "/collections/" + collection + "/query"

	body, err = call(ctx, r, "POST", path, query(groups-1, 1000, true))
	if err != nil {
		return row, err
	}
	if err := verify(body, size, groups-1, 1000, true); err != nil {
		return row, err
	}
	started = time.Now()
	for group := range queries {
		queryStart := time.Now()
		body, err := call(ctx, r, "POST", path, query(group, 10, false))
		latency := time.Since(queryStart)
		if err != nil {
			return row, err
		}
		if err := verify(body, size, group, 10, false); err != nil {
			return row, err
		}
		row.latencies = append(row.latencies, float64(latency)/float64(time.Millisecond))
	}
	row.elapsed = time.Since(started)
	return row, nil
}

func verify(body []byte, size, group, limit int, count bool) error {
	var p page
	if err := json.Unmarshal(body, &p); err != nil {
		return err
	}
	matches := (size-1-group)/groups + 1
	if len(p.Items) != min(matches, limit) || count && (!p.Count.Exact || p.Count.Value != strconv.Itoa(matches)) {
		return fmt.Errorf("incorrect match count for term %d", group)
	}
	seen := map[int]bool{}
	previous, lowestHigh := math.Inf(1), math.Inf(1)
	highCount := (matches + 1) / 2
	for i, item := range p.Items {
		n := item.Document.N
		if n < 0 || n >= size || n%groups != group || seen[n] || item.ID != fmt.Sprintf("%026d", n+1) {
			return fmt.Errorf("incorrect or repeated match for term %d", group)
		}
		seen[n] = true
		if item.Score == nil || math.IsNaN(*item.Score) || math.IsInf(*item.Score, 0) || *item.Score > previous {
			return fmt.Errorf("missing, nonfinite or ascending relevance score for term %d", group)
		}
		if i > 0 && *item.Score == previous && item.ID <= p.Items[i-1].ID {
			return fmt.Errorf("equal-score matches must use ascending ID order")
		}
		if i < min(highCount, limit) && n/groups%2 != 0 {
			return fmt.Errorf("low-frequency match ranked above high-frequency match for term %d", group)
		}
		if n/groups%2 == 0 {
			lowestHigh = *item.Score
		} else if *item.Score >= lowestHigh {
			return fmt.Errorf("term frequency did not produce a strictly higher relevance score")
		}
		previous = *item.Score
	}
	return nil
}

func call(ctx context.Context, r *benchclient.Region, method, path string, value any) ([]byte, error) {
	body, err := json.Marshal(value)
	if err != nil {
		return nil, err
	}
	response, err := r.Do(ctx, method, path, body, "application/json")
	if err != nil {
		return nil, err
	}
	if response.Status >= 300 {

		var failure struct {
			Error struct {
				Code string `json:"code"`
			} `json:"error"`
		}
		if json.Unmarshal(response.Body, &failure) == nil {
			switch failure.Error.Code {
			case "capacity", "storage_busy", "copy_faulted", "region_unavailable", "remote_region_unavailable", "confirmation_busy", "catalog_stale", "write_outcome_unknown", "unavailable":
				return nil, fmt.Errorf("%s %s: HTTP %d (%s)", method, path, response.Status, failure.Error.Code)
			}
		}
		return nil, fmt.Errorf("%s %s: HTTP %d", method, path, response.Status)
	}
	return response.Body, nil
}

func report(rows []result) string {
	var b strings.Builder
	host, _ := os.Hostname()
	sha, _ := exec.Command("git", "rev-parse", "HEAD").Output()
	fmt.Fprintf(&b, "# Full-text index build and ranked HTTP queries\n\nMeasured: %s. Host: %s (%s, %d logical CPUs). Go: %s. Commit: `%s`.\n\n", time.Now().UTC().Format(time.RFC3339), host, runtime.GOARCH, runtime.NumCPU(), runtime.Version(), strings.TrimSpace(string(sha)))
	b.WriteString("Local single-region test host (gru), Release, client and server on one host; requests target gru. Test-only issuer and temporary database; no production credentials. HTTP serialization, network, server query and response reads are included. This is local API evidence, not OCI capacity. The existing host freezes query deadlines; production timeouts are not measured.\n\n")
	b.WriteString("Each size has three independent fresh collections, populated before timing index creation (`kind:text`, `fields:[article.body]`). Documents contain integer `n` and nested `article.body`: 32 words, 129 evenly distributed alphabetic topic tokens, one or four topic occurrences (alternating groups), and orchard filler. Bytes are the summed uncompressed UTF-8 document JSON sizes; HTTP envelopes and index storage are excluded.\n\n")
	b.WriteString("One unmeasured disjoint-term query verifies the complete matching ID set and strict frequency ranking. Each repeat measures 128 different search terms exactly once, limit 10, sorted by `$score` descending; no measured query is served from a previous identical query result. Every response must contain only expected distinct IDs and finite descending item scores, with high-frequency matches first. Throughput is sequential closed-loop successful queries / elapsed time, including client correctness checks; it is not maximum concurrent throughput. Latencies exclude those checks. No request retries.\n\n")
	b.WriteString("Only completed repeats appear below. A successful command must produce repeats 1–3 for every requested size.\n\n| Documents | JSON bytes | Repeat | Build ms | Docs/build s | Queries | Query p50 ms | p95 ms | p99 ms | Queries/s |\n| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |\n")
	for _, r := range rows {
		sorted := slices.Sorted(slices.Values(r.latencies))
		p := func(q float64) float64 { return sorted[int(math.Ceil(q*float64(len(sorted))))-1] }
		fmt.Fprintf(&b, "| %d | %d | %d | %.3f | %.1f | %d | %.3f | %.3f | %.3f | %.2f |\n", r.size, r.bytes, r.repeat, float64(r.build)/float64(time.Millisecond), float64(r.size)/r.build.Seconds(), len(sorted), p(.50), p(.95), p(.99), float64(len(sorted))/r.elapsed.Seconds())
	}
	b.WriteString("\nRaw samples below are milliseconds in term order 0–127; index times above are raw individual builds.\n\n```json\n")
	for _, r := range rows {
		raw, _ := json.Marshal(map[string]any{"documents": r.size, "repeat": r.repeat, "query_ms": r.latencies, "query_window_ms": float64(r.elapsed) / float64(time.Millisecond)})
		b.Write(raw)
		b.WriteByte('\n')
	}
	b.WriteString("```\n\nReproduce after building the native and Release test host from this commit: `nice -n 19 bash benchmark/scripts/fts-local.sh`. See `benchmark/cmd/fts-bench/README.md`.\n")
	return b.String()
}
