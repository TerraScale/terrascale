package main

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"math"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"slices"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"terrascale.tech/ts-benchmark/internal/benchclient"
)

type row struct {
	repeat    int
	build     time.Duration
	latencies []float64
	recall    []float64
}

func main() {
	targets := flag.String("targets", "", "targets file from the local cluster host")
	keyFile := flag.String("key-file", "", "instead of --targets: a 0600 JSON file with api, issuer, project, database, key_id, key and scope; requests pin region gru")
	data := flag.String("data", "", "directory with corpus.f32 and queries.f32")
	dims := flag.Int("dims", 768, "vector dimensions")
	docs := flag.Int("docs", 100000, "documents to load")
	queries := flag.Int("queries", 200, "queries per repeat")
	repeats := flag.Int("repeats", 3, "query passes, each running every query once")
	rebuildEach := flag.Bool("rebuild-each", true, "drop and rebuild the index before every pass, not only the first")
	cleanup := flag.Bool("cleanup", false, "delete the collection after the last pass, with its index")
	offset := flag.Int("query-offset", 0, "skip this many query vectors, so a rerun does not repeat cached queries")
	skipBuild := flag.Bool("skip-build", false, "with --collection: query the index already built on it")
	limit := flag.Int("limit", 10, "near query limit (k)")
	collection := flag.String("collection", "", "reuse a loaded collection (default: load a new one)")
	note := flag.String("note", "", "environment sentence for the report")
	out := flag.String("out", "", "Markdown output file")
	flag.Parse()

	corpus := must(readVectors(filepath.Join(*data, "corpus.f32"), *dims, *docs))

	probes := must(readVectors(filepath.Join(*data, "queries.f32"), *dims, *offset+*repeats**queries))[*offset:]
	var t benchclient.Targets
	var credentials *apiKey
	if *keyFile != "" {
		credentials = must(readKey(*keyFile))
		t = benchclient.Targets{Kind: "remote", Project: credentials.Project, Database: credentials.Database,
			Regions: map[string]benchclient.RegionTarget{"api": {URL: credentials.API, Token: must(credentials.token())}}}
	} else {
		t = must(benchclient.LoadTargets(*targets))
	}
	var region *benchclient.Region
	for _, name := range slices.Sorted(func(yield func(string) bool) {
		for name := range t.Regions {
			if !yield(name) {
				return
			}
		}
	}) {
		region = must(benchclient.NewRegion(name, t, 8))
		if credentials != nil {
			region.Pin = "gru"
		}
		break
	}
	ctx := context.Background()
	if credentials != nil {

		go func() {
			for range time.Tick(30 * time.Second) {
				if token, err := credentials.token(); err == nil {
					region.SetToken(token)
				} else {
					log.Print("token refresh failed; keeping the current token")
				}
			}
		}()
	}

	name := *collection
	if name == "" {
		name = "ann" + strconv.FormatInt(time.Now().Unix(), 36)
		must(call(ctx, region, "POST", "/collections", map[string]any{"name": name}))
		log.Printf("collection %s", name)
		load(ctx, region, name, corpus)
	}
	truth := exact(corpus, probes, *limit)
	log.Print("exact neighbours computed")

	var rows []row
	for r := 1; r <= *repeats; r++ {
		current := row{repeat: r}
		if (r == 1 && !*skipBuild) || (r > 1 && *rebuildEach) {
			current.build = buildIndex(ctx, region, name, *dims)
		}
		for i := (r - 1) * *queries; i < r**queries; i++ {
			q := probes[i]
			queryStart := time.Now()
			body := must(call(ctx, region, "POST", "/collections/"+name+"/query", map[string]any{
				"sort": []any{map[string]any{"near": map[string]any{"path": "/embedding", "vector": q}}}, "limit": *limit,
			}))
			current.latencies = append(current.latencies, float64(time.Since(queryStart))/float64(time.Millisecond))
			var page struct {
				Items []struct {
					ID string `json:"id"`
				} `json:"items"`
			}
			must(0, json.Unmarshal(body, &page))
			hits := 0
			for _, item := range page.Items {
				if slices.Contains(truth[i], item.ID) {
					hits++
				}
			}
			current.recall = append(current.recall, float64(hits)/float64(len(truth[i])))
		}
		log.Printf("repeat %d: build %s, p50 %.1f ms, recall %.4f", r, current.build, percentile(current.latencies, 50), mean(current.recall))
		rows = append(rows, current)
	}
	if *cleanup {
		size := must(call(ctx, region, "GET", "/collections/"+name, nil))
		log.Printf("collection before cleanup: %s", size)
		must(call(ctx, region, "DELETE", "/collections/"+name, nil))
		log.Print("collection deleted")
	}
	must(0, os.WriteFile(*out, []byte(report(rows, *docs, *dims, *queries, *limit, region.Name, *note, *rebuildEach)), 0o644))
}

func buildIndex(ctx context.Context, region *benchclient.Region, name string, dims int) time.Duration {
	_, _ = call(ctx, region, "DELETE", "/indexes/"+name+"_vec", nil)
	started := time.Now()
	must(call(ctx, region, "POST", "/indexes", map[string]any{
		"name": name + "_vec", "collection": name, "kind": "vector", "fields": []string{"embedding"}, "dimensions": dims,
	}))

	for {
		body := must(call(ctx, region, "GET", "/indexes?collection="+name, nil))
		var list struct {
			Items []struct {
				State string `json:"state"`
			} `json:"items"`
		}
		must(0, json.Unmarshal(body, &list))
		if len(list.Items) == 1 && list.Items[0].State == "active" {
			return time.Since(started)
		}
		time.Sleep(250 * time.Millisecond)
	}
}

type apiKey struct {
	API      string `json:"api"`
	Issuer   string `json:"issuer"`
	Project  string `json:"project"`
	Database string `json:"database"`
	KeyID    string `json:"key_id"`
	Key      string `json:"key"`
	Scope    string `json:"scope"`
}

func readKey(path string) (*apiKey, error) {
	info, err := os.Stat(path)
	if err != nil {
		return nil, err
	}
	if info.Mode().Perm()&0o077 != 0 {
		return nil, fmt.Errorf("%s must not be readable by group or others (chmod 0600)", path)
	}
	raw, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var key apiKey
	if err := json.Unmarshal(raw, &key); err != nil || key.API == "" || key.Issuer == "" || key.KeyID == "" || key.Key == "" {
		return nil, fmt.Errorf("%s: needs api, issuer, project, database, key_id, key and scope", path)
	}
	return &key, nil
}

func (k *apiKey) token() (string, error) {
	response, err := http.PostForm(k.Issuer+"/oauth/token", url.Values{
		"grant_type": {"client_credentials"}, "client_id": {k.KeyID}, "client_secret": {k.Key}, "project": {k.Project}, "scope": {k.Scope},
	})
	if err != nil {
		return "", err
	}
	defer response.Body.Close()
	var body struct {
		AccessToken string `json:"access_token"`
	}
	if response.StatusCode != http.StatusOK || json.NewDecoder(response.Body).Decode(&body) != nil || body.AccessToken == "" {
		return "", fmt.Errorf("token request: HTTP %d", response.StatusCode)
	}
	return body.AccessToken, nil
}

func id(n int) string { return fmt.Sprintf("%026d", n+1) }

func load(ctx context.Context, r *benchclient.Region, collection string, corpus [][]float32) {
	const batch = 100
	work := make(chan int)
	var wg sync.WaitGroup
	for range 4 {
		wg.Go(func() {
			for start := range work {
				var ops []any
				for n := start; n < min(start+batch, len(corpus)); n++ {
					ops = append(ops, map[string]any{"op": "put", "collection": collection, "id": id(n), "document": map[string]any{"n": n, "embedding": corpus[n]}})
				}
				for attempt := 1; ; attempt++ {
					_, err := call(ctx, r, "POST", "/transactions", map[string]any{"ops": ops})
					if err == nil {
						break
					}
					if attempt == 5 {
						log.Fatal(err)
					}
					time.Sleep(time.Duration(attempt) * time.Second)
				}
			}
		})
	}
	for start := 0; start < len(corpus); start += batch {
		work <- start
		if start%10000 == 0 {
			log.Printf("loading %d", start)
		}
	}
	close(work)
	wg.Wait()
}

func exact(corpus, queries [][]float32, k int) [][]string {
	norms := make([]float64, len(corpus))
	for i, v := range corpus {
		norms[i] = norm(v)
	}
	out := make([][]string, len(queries))
	var wg sync.WaitGroup
	for qi, q := range queries {
		wg.Go(func() {
			type scored struct {
				n int
				d float64
			}
			all := make([]scored, len(corpus))
			qn := norm(q)
			for i, v := range corpus {
				var dot float64
				for j := range v {
					dot += float64(v[j]) * float64(q[j])
				}
				all[i] = scored{i, 1 - dot/(norms[i]*qn)}
			}
			sort.Slice(all, func(a, b int) bool { return all[a].d < all[b].d || all[a].d == all[b].d && all[a].n < all[b].n })
			for _, s := range all[:k] {
				out[qi] = append(out[qi], id(s.n))
			}
		})
	}
	wg.Wait()
	return out
}

func norm(v []float32) float64 {
	var s float64
	for _, x := range v {
		s += float64(x) * float64(x)
	}
	return math.Sqrt(s)
}

func readVectors(path string, dims, count int) ([][]float32, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	if len(raw) < dims*4*count {
		return nil, fmt.Errorf("%s holds fewer than %d vectors of %d dimensions", path, count, dims)
	}
	vectors := make([][]float32, count)
	for i := range vectors {
		vectors[i] = make([]float32, dims)
		for j := range dims {
			vectors[i][j] = math.Float32frombits(binary.LittleEndian.Uint32(raw[(i*dims+j)*4:]))
		}
	}
	return vectors, nil
}

func call(ctx context.Context, r *benchclient.Region, method, path string, value any) ([]byte, error) {
	var body []byte
	if value != nil {
		var err error
		if body, err = json.Marshal(value); err != nil {
			return nil, err
		}
	}
	response, err := r.Do(ctx, method, path, body, "application/json")
	if err != nil {
		return nil, err
	}
	if response.Status >= 300 && !(method == http.MethodDelete && response.Status == http.StatusNotFound) {

		return nil, fmt.Errorf("%s %s: HTTP %d", method, path, response.Status)
	}
	return response.Body, nil
}

func percentile(values []float64, p float64) float64 {
	sorted := slices.Sorted(slices.Values(values))
	return sorted[min(len(sorted)-1, int(math.Ceil(p/100*float64(len(sorted))))-1)]
}

func mean(values []float64) float64 {
	var s float64
	for _, v := range values {
		s += v
	}
	return s / float64(len(values))
}

func report(rows []row, docs, dims, queries, limit int, region, note string, rebuildEach bool) string {
	var b strings.Builder
	fmt.Fprintf(&b, "# Vector index (ANN) build, near-query latency and recall\n\nMeasured: %s. Region %s. %s\n\n", time.Now().UTC().Format(time.RFC3339), region, note)
	build := "the first repeat builds the `kind: vector` index on the loaded collection"
	if rebuildEach {
		build = "each repeat drops and rebuilds the `kind: vector` index on the loaded collection"
	}
	fmt.Fprintf(&b, "%d documents of %d-dim embeddings; %s, "+
		"then each pass runs %d held-out query vectors of its own, once each (cosine, limit %d, no filter), sequentially. Latency is end-to-end HTTP. "+
		"Build ms runs from the create request until the index lists as active. Recall@%d is against exact cosine neighbours computed by the client over the same float32 values.\n\n", docs, dims, build, queries, limit, limit)
	b.WriteString("| Repeat | Build ms | Queries | p50 ms | p95 ms | p99 ms | Recall@k mean | Recall@k min |\n| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |\n")
	for _, r := range rows {
		buildMs := "-"
		if r.build > 0 {
			buildMs = fmt.Sprintf("%.0f", float64(r.build)/float64(time.Millisecond))
		}
		fmt.Fprintf(&b, "| %d | %s | %d | %.1f | %.1f | %.1f | %.4f | %.2f |\n", r.repeat, buildMs, len(r.latencies),
			percentile(r.latencies, 50), percentile(r.latencies, 95), percentile(r.latencies, 99), mean(r.recall), slices.Min(r.recall))
	}
	return b.String()
}

func must[T any](value T, err error) T {
	if err != nil {
		log.Fatal(err)
	}
	return value
}
