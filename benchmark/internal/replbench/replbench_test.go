package replbench

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"slices"
	"strings"
	"sync"
	"testing"
	"time"
)

func TestCanonicalSortsKeysAndKeepsNumbers(t *testing.T) {
	got, err := Canonical([]byte(` {"b": [1, {"z":1,"a":"<&>"}], "a": 1.50, "c": null} `))
	if err != nil {
		t.Fatal(err)
	}
	if want := `{"a":1.50,"b":[1,{"a":"<&>","z":1}],"c":null}`; string(got) != want {
		t.Fatalf("got %s, want %s", got, want)
	}
}

func TestBodyIsCanonicalExactSizeAndRepeatable(t *testing.T) {
	for _, size := range []int{1, 64, 256, 8192} {
		b := Body(7, 12345, "gru", size)
		c, err := Canonical(b)
		if err != nil || string(c) != string(b) {
			t.Fatalf("size %d: body is not canonical JSON: %s (%v)", size, b, err)
		}
		if size >= 64 && len(b) != size {
			t.Fatalf("size %d: got %d bytes", size, len(b))
		}
		if string(Body(7, 12345, "gru", size)) != string(b) || string(Body(8, 12345, "gru", size)) == string(b) && size > 64 {
			t.Fatalf("size %d: body must depend on seed and index only", size)
		}
	}
}

func TestParseSizes(t *testing.T) {
	got, err := ParseSizes("256:70, 1024:25,8192")
	if err != nil || !slices.Equal(got, []SizeWeight{{256, 70}, {1024, 25}, {8192, 1}}) {
		t.Fatalf("got %v %v", got, err)
	}
	for _, bad := range []string{"", "0", "x:1", "256:0", "2000000"} {
		if _, err := ParseSizes(bad); err == nil {
			t.Fatalf("%q: want an error", bad)
		}
	}
}

// fakeCluster is regions replicating every write at once, in order; drop names a document one region never gets.
type fakeCluster struct {
	mu   sync.Mutex
	docs map[string]map[string]map[string]json.RawMessage // region -> collection -> id -> document
	drop func(region, collection, id string) bool
}

func (f *fakeCluster) apply(from, coll, id string, doc json.RawMessage) {
	set := func(region string) {
		f.mu.Lock()
		defer f.mu.Unlock()
		if f.docs[region][coll] == nil {
			f.docs[region][coll] = map[string]json.RawMessage{}
		}
		if doc == nil {
			delete(f.docs[region][coll], id)
		} else {
			f.docs[region][coll][id] = doc
		}
	}
	set(from)
	for region := range f.docs {
		if region != from && (f.drop == nil || !f.drop(region, coll, id)) {
			set(region)
		}
	}
}

func (f *fakeCluster) handler(region string) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		parts := strings.Split(strings.TrimPrefix(r.URL.Path, "/v1/projects/p/databases/d"), "/")
		body, _ := io.ReadAll(r.Body)
		switch {
		case r.Method == http.MethodPost && r.URL.Path == "/v1/projects/p/databases/d/collections":
			var c struct{ Name string }
			_ = json.Unmarshal(body, &c)
			for reg := range f.docs {
				f.mu.Lock()
				f.docs[reg][c.Name] = map[string]json.RawMessage{}
				f.mu.Unlock()
			}
		case r.Method == http.MethodPost && parts[1] == "transactions":
			var tx struct {
				Ops []struct {
					Collection, ID string
					Document       json.RawMessage
				}
			}
			_ = json.Unmarshal(body, &tx)
			for _, op := range tx.Ops {
				f.apply(region, op.Collection, op.ID, op.Document)
			}
		case len(parts) == 3 && r.Method == http.MethodGet: // /collections/{c}
		case len(parts) == 4 && r.Method == http.MethodGet: // list
			f.mu.Lock()
			coll := f.docs[region][parts[2]]
			type item struct {
				ID       string          `json:"id"`
				Document json.RawMessage `json:"document"`
			}
			items := []item{}
			for id, d := range coll {
				items = append(items, item{id, d})
			}
			f.mu.Unlock()
			_ = json.NewEncoder(w).Encode(map[string]any{"items": items, "next_cursor": nil})
		case len(parts) == 5: // /collections/{c}/documents/by-key
			coll, id := parts[2], r.URL.Query().Get("id")
			f.mu.Lock()
			cur, ok := f.docs[region][coll][id]
			f.mu.Unlock()
			switch r.Method {
			case http.MethodGet:
				if !ok {
					w.WriteHeader(http.StatusNotFound)
					return
				}
				_ = json.NewEncoder(w).Encode(map[string]any{"document": cur})
			case http.MethodPut:
				f.apply(region, coll, id, body)
			case http.MethodDelete:
				f.apply(region, coll, id, nil)
			case http.MethodPatch:
				var doc, patch map[string]any
				_ = json.Unmarshal(cur, &doc)
				_ = json.Unmarshal(body, &patch)
				for k, v := range patch {
					doc[k] = v
				}
				merged, _ := json.Marshal(doc)
				f.apply(region, coll, id, merged)
			}
		default:
			w.WriteHeader(http.StatusNotFound)
		}
	})
}

// fakeRun is what the runner recorded against the fake cluster.
type fakeRun struct {
	conv      Convergence
	conflicts ConflictOutcome
	created   int64
	lag       Summary
}

func runFake(t *testing.T, drop func(region, collection, id string) bool) fakeRun {
	t.Helper()
	f := &fakeCluster{docs: map[string]map[string]map[string]json.RawMessage{}, drop: drop}
	tg := Targets{Kind: "test", Project: "p", Database: "d", Regions: map[string]RegionTarget{}}
	for _, region := range []string{"gru", "sjc", "fra"} {
		f.docs[region] = map[string]map[string]json.RawMessage{}
		srv := httptest.NewServer(f.handler(region))
		t.Cleanup(srv.Close)
		tg.Regions[region] = RegionTarget{URL: srv.URL, Token: "t"}
	}
	cfg := Config{Docs: 600, Sizes: []SizeWeight{{128, 1}, {512, 1}}, Batch: 4, Concurrency: 3, DeletePct: 10, ConflictDocs: 5, ConflictRounds: 3,
		ProbeEvery: 20 * time.Millisecond, Converge: 5 * time.Second, Seed: 1}
	r, err := NewRunner(cfg, tg, t.Logf)
	if err != nil {
		t.Fatal(err)
	}
	ctx := context.Background()
	var res fakeRun
	if err := r.Setup(ctx); err != nil {
		t.Fatal(err)
	}
	if _, err := r.Load(ctx); err != nil {
		t.Fatal(err)
	}
	if res.conv, err = r.Converge(ctx, time.Now()); err != nil {
		t.Fatal(err)
	}
	if res.conflicts, err = r.CheckConflicts(ctx); err != nil {
		t.Fatal(err)
	}
	for _, n := range r.created {
		res.created += n.Load()
	}
	res.lag = r.probes.lag["gru->sjc"].Summary()
	return res
}

func TestRunnerVerifiesEveryRegionExactly(t *testing.T) {
	res := runFake(t, nil)
	if !res.conv.Converged || res.conv.Unknown != 0 {
		t.Fatalf("want every region exact: %+v", res.conv)
	}
	if res.created != 600 || res.conv.Expected+res.conv.Deleted != 600 || res.conv.Deleted == 0 {
		t.Fatalf("created %d, expected %d, deleted %d", res.created, res.conv.Expected, res.conv.Deleted)
	}
	if res.lag.Count == 0 || res.conflicts.Identical != 5 || res.conflicts.SameField["invalid"] != 0 {
		t.Fatalf("lag %+v conflicts %+v", res.lag, res.conflicts)
	}
}

func TestRunnerReportsADocumentARegionNeverGot(t *testing.T) {
	var once sync.Once
	var lost string
	res := runFake(t, func(region, collection, id string) bool {
		if region != "fra" || strings.HasSuffix(collection, "-probe") || strings.HasSuffix(collection, "-conflict") {
			return false
		}
		once.Do(func() { lost = id })
		return id == lost
	})
	if fra := res.conv.Regions["fra"]; res.conv.Converged || fra.Missing != 1 || !slices.Contains(fra.Examples, "missing "+lost) {
		t.Fatalf("the document %s fra never got went unnoticed: %+v", lost, fra)
	}
}
