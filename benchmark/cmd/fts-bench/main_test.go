package main

import (
	"encoding/json/v2"
	"fmt"
	"strings"
	"testing"
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
