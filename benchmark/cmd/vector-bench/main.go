package main

import (
	"bytes"
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"math/rand/v2"
	"net/http"
	"os"
	"strconv"
	"strings"
	"time"

	"terrascale.tech/ts-benchmark/internal/benchclient"
)

const batch = 250

func main() {
	targets := flag.String("targets", "", "targets file from the local cluster host")
	sizes := flag.String("sizes", "10000,100000", "comma-separated collection sizes, ascending")
	dims := flag.Int("dims", 128, "vector dimensions")
	repeats := flag.Int("repeats", 3, "queries per size and metric")
	limit := flag.Int("limit", 10, "near query limit")
	out := flag.String("out", "", "Markdown output file")
	flag.Parse()

	t, err := benchclient.LoadTargets(*targets)
	if err != nil {
		log.Fatal(err)
	}
	var region *benchclient.Region
	for name := range t.Regions {
		if region, err = benchclient.NewRegion(name, t, 4); err != nil {
			log.Fatal(err)
		}
		break
	}
	if region == nil {
		log.Fatal("the targets file names no region")
	}

	ctx := context.Background()
	collection := "vectors" + strconv.FormatInt(time.Now().Unix(), 36)
	must(call(ctx, region, "/collections", map[string]any{"name": collection}))
	rng := rand.New(rand.NewPCG(1, 2))
	vector := func() []float64 {
		v := make([]float64, *dims)
		for i := range v {
			v[i] = float64(float32(rng.NormFloat64()))
		}
		return v
	}

	var rows []string
	loaded := 0
	for _, field := range strings.Split(*sizes, ",") {
		size, err := strconv.Atoi(strings.TrimSpace(field))
		if err != nil {
			log.Fatal(err)
		}
		for loaded < size {
			ops := make([]map[string]any, 0, batch)
			for ; loaded < size && len(ops) < batch; loaded++ {
				ops = append(ops, map[string]any{"op": "put", "collection": collection, "id": fmt.Sprintf("v%07d", loaded), "document": map[string]any{"n": loaded, "embedding": vector()}})
			}

			for attempt := 1; ; attempt++ {
				if err := call(ctx, region, "/transactions", map[string]any{"ops": ops}); err == nil || attempt == 5 {
					must(err)
					break
				}
				time.Sleep(time.Second)
			}
		}
		log.Printf("%d documents loaded", loaded)
		for _, metric := range []string{"cosine", "l2"} {
			for r := 1; r <= *repeats; r++ {

				query := vector()
				started := time.Now()
				status, body, err := callStatus(ctx, region, "/collections/"+collection+"/query", map[string]any{
					"sort":  []any{map[string]any{"near": map[string]any{"path": "/embedding", "vector": query, "metric": metric}}},
					"limit": *limit,
				})
				elapsed := time.Since(started)
				if err != nil {
					log.Fatal(err)
				}
				result := "ok"
				if status != http.StatusOK {
					result = fmt.Sprintf("%d %s", status, strings.TrimSpace(string(body)))
				}
				rows = append(rows, fmt.Sprintf("| %d | %d | %s | %d | %.1f | %s |", size, *dims, metric, r, float64(elapsed.Microseconds())/1000, result))
				log.Print(rows[len(rows)-1])
			}
		}
	}

	var md bytes.Buffer
	fmt.Fprintf(&md, "# Brute-force near-sort query latency\n\nDate: %s. Region %s of a local single-region TerraBase host (LocalBenchmarkHost, Release), "+
		"client on the same host. Random float32 vectors, limit %d, no filter, end-to-end HTTP latency per query, a new query vector each repeat (no result-cache hit).\n\n", time.Now().UTC().Format(time.RFC3339), region.Name, *limit)
	md.WriteString("| Documents | Dims | Metric | Repeat | Latency ms | Result |\n| ---: | ---: | --- | ---: | ---: | --- |\n")
	md.WriteString(strings.Join(rows, "\n") + "\n")
	if err := os.WriteFile(*out, md.Bytes(), 0o644); err != nil {
		log.Fatal(err)
	}
}

func call(ctx context.Context, region *benchclient.Region, path string, body any) error {
	status, resp, err := callStatus(ctx, region, path, body)
	if err == nil && status >= 300 {
		err = fmt.Errorf("POST %s: %d %s", path, status, resp)
	}
	return err
}

func callStatus(ctx context.Context, region *benchclient.Region, path string, body any) (int, []byte, error) {
	payload, err := json.Marshal(body)
	if err != nil {
		return 0, nil, err
	}
	resp, err := region.Do(ctx, http.MethodPost, path, payload, "application/json")
	return resp.Status, resp.Body, err
}

func must(err error) {
	if err != nil {
		log.Fatal(err)
	}
}
