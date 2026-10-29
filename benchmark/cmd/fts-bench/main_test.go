package main

import (
	"encoding/json/v2"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"

	"terrascale.tech/ts-benchmark/internal/benchclient"
)

func TestCorpusAndRankOracle(t *testing.T) {
	t.Parallel()
	var items []map[string]any
	for n := 0; n < 2*groups; n += groups {
		doc := document(n)
		words := strings.Fields(doc["article"].(map[string]any)["body"].(string))
		frequency := 1
		if n == 0 {
			frequency = 4
		}
		if len(words) != 32 || strings.Count(strings.Join(words, " "), term(0)) != frequency {
			t.Fatal("corpus must have equal token counts and distinct term frequencies")
		}
		items = append(items, map[string]any{"id": fmt.Sprintf("%026d", n+1), "score": float64(frequency), "document": doc})
	}
	encode := func(values []map[string]any) []byte {
		body, err := json.Marshal(map[string]any{"items": values, "count": map[string]any{"value": "2", "exact": true}})
		if err != nil {
			t.Fatal(err)
		}
		return body
	}
	if err := verify(encode(items), 2*groups, 0, 1000, true); err != nil {
		t.Fatal(err)
	}
	for _, fault := range []string{"score", "match", "duplicate", "ranking"} {
		original := items[0]
		modified := []map[string]any{{"id": original["id"], "score": original["score"], "document": original["document"]}, items[1]}
		switch fault {
		case "score":
			delete(modified[0], "score")
		case "match":
			modified[0]["document"] = document(1)
		case "duplicate":
			modified[0] = modified[1]
		case "ranking":
			modified[0]["score"] = 0
		}
		if err := verify(encode(modified), 2*groups, 0, 1000, true); err == nil {
			t.Fatalf("incorrect search response passed the benchmark oracle: %s", fault)
		}
	}
}

func TestWireQuery(t *testing.T) {
	t.Parallel()
	q := query(0, 10, false)
	search := q["filter"].(map[string]any)["search"].([]any)
	sort := q["sort"].([]any)[0].(map[string]any)
	if search[0] != "/article/body" || search[1] != term(0) || sort["path"] != "$score" || sort["order"] != "desc" {
		t.Fatal("benchmark must use RFC6901 search paths and descending score sort")
	}
}

func TestFailureDiagnosticsDoNotExposePrivateResponseFields(t *testing.T) {
	t.Parallel()
	for _, code := range []string{"storage_busy", "write_outcome_unknown", "private-credential"} {
		t.Run(code, func(t *testing.T) {
			t.Parallel()
			var requests atomic.Int64
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				requests.Add(1)
				w.WriteHeader(http.StatusServiceUnavailable)
				fmt.Fprintf(w, `{"error":{"code":%q,"message":"private-message","machine":"private-machine"}}`, code)
			}))
			t.Cleanup(server.Close)
			region, err := benchclient.NewRegion("gru", benchclient.Targets{Project: "project", Database: "database", Regions: map[string]benchclient.RegionTarget{"gru": {URL: server.URL, Token: "private-token"}}}, 1)
			if err != nil {
				t.Fatal(err)
			}
			_, err = call(t.Context(), region, "POST", "/transactions", nil)
			want := "POST /transactions: HTTP 503"
			if code != "private-credential" {
				want += " (" + code + ")"
			}
			if err == nil || err.Error() != want || requests.Load() != 1 {
				t.Fatalf("expected one request and sanitized error %q; got %d requests, %v", want, requests.Load(), err)
			}
		})
	}
}
