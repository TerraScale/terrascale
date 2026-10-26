package v1_test

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

func TestVectorAndGeoNearProducerLimits(t *testing.T) {
	for _, geo := range []bool{false, true} {
		for _, limit := range []int{101, 1000} {
			t.Run(fmt.Sprintf("geo=%t/limit=%d", geo, limit), func(t *testing.T) {
				sort := tb.Near("/value", []float32{1}, tb.Cosine)
				near := `"vector":[1],"metric":"cosine"`
				if geo {
					sort, near = tb.NearPoint("/value", 0, 0), `"lat":0,"lon":0`
				}
				body := fmt.Sprintf(`{"sort":[{"near":{"path":"/value",%s}}],"limit":%d}`, near, limit)
				col := parityClient(t, http.MethodPost, parityDatabase+"/collections/items/query", "", body, `{"items":[],"next_cursor":null}`, http.StatusOK).Database(parityProject, "main").Collection("items")
				out, err := col.Query(t.Context(), tb.Query{Sort: []tb.SortKey{sort}, Limit: limit})
				if err != nil || out.NextCursor != "" {
					t.Fatalf("near = %+v, %v", out, err)
				}
			})
		}
	}
}

func TestVectorIndexCreateAndListContract(t *testing.T) {
	for _, tc := range []struct {
		state string
		dim   int
	}{{"building", 1}, {"active", 4096}} {
		t.Run(tc.state, func(t *testing.T) {
			index := fmt.Sprintf(`{"name":"items_vector","collection":"items","fields":["embedding"],"state":%q,"kind":"vector","dimensions":%d}`, tc.state, tc.dim)
			body := fmt.Sprintf(`{"collection":"items","fields":["embedding"],"kind":"vector","dimensions":%d}`, tc.dim)
			db := parityClient(t, http.MethodPost, parityDatabase+"/indexes", "", body, index, http.StatusCreated).Database(parityProject, "main")
			created, err := db.CreateIndex(t.Context(), tb.IndexRequest{Collection: "items", Fields: []string{"embedding"}, Kind: tb.IndexVector, Dimensions: &tc.dim})
			if err != nil {
				t.Fatal(err)
			}
			db = parityClient(t, http.MethodGet, parityDatabase+"/indexes", "", "", `{"items":[`+index+`]}`, http.StatusOK).Database(parityProject, "main")
			listed, err := db.Indexes(t.Context())
			if err != nil || len(listed) != 1 {
				t.Fatalf("list = %+v, %v", listed, err)
			}
			for _, out := range []tb.Index{*created, listed[0]} {
				if out.Kind != tb.IndexVector || out.State != tc.state || out.Dimensions == nil || *out.Dimensions != tc.dim {
					t.Fatalf("vector index = %+v", out)
				}
			}
		})
	}
}

func TestVectorInvalidContractsDoNotSend(t *testing.T) {
	c, err := tb.New(tb.Config{BaseURL: "https://tb.example", Token: "header.project.signature", HTTPClient: &http.Client{Transport: failTransport{t}}})
	if err != nil {
		t.Fatal(err)
	}
	db := c.Database(parityProject, "main")
	zero, excessive, dim := 0, 4097, 2
	for _, in := range []tb.IndexRequest{
		{Collection: "items", Fields: []string{"embedding"}, Kind: tb.IndexVector},
		{Collection: "items", Fields: []string{"embedding"}, Kind: tb.IndexVector, Dimensions: &zero},
		{Collection: "items", Fields: []string{"embedding"}, Kind: tb.IndexVector, Dimensions: &excessive},
		{Collection: "items", Fields: []string{}, Kind: tb.IndexVector, Dimensions: &dim},
		{Collection: "items", Fields: []string{"a", "b"}, Kind: tb.IndexVector, Dimensions: &dim},
		{Collection: "items", Fields: []string{"embedding"}, Kind: tb.IndexBTree, Dimensions: &dim},
		{Collection: "items", Fields: []string{"embedding"}, Kind: tb.IndexText, Dimensions: &dim},
		{Collection: "items", Fields: []string{"embedding"}, Dimensions: &dim},
		{Collection: "items", Fields: []string{"embedding"}, Kind: "unknown"},
	} {
		if _, err := db.CreateIndex(t.Context(), in); err == nil {
			t.Errorf("accepted index %+v", in)
		}
	}
	for _, sort := range []tb.SortKey{tb.Near("/value", []float32{1}, tb.Cosine), tb.NearPoint("/value", 0, 0)} {
		for _, q := range []tb.Query{
			{Sort: []tb.SortKey{sort}, Limit: 1001},
			{Sort: []tb.SortKey{sort}, Limit: -1},
			{Sort: []tb.SortKey{sort}, Cursor: "next"},
			{Sort: []tb.SortKey{sort, tb.Asc("/name")}},
			{Sort: []tb.SortKey{sort}, Filter: tb.Search("/title", "fox")},
		} {
			if _, err := db.Collection("items").Query(t.Context(), q); err == nil || !strings.Contains(err.Error(), "near") {
				t.Errorf("near accepted: %v", err)
			}
		}
	}
}

func TestVectorIndexFailureIsNotRetriedAndContextIsPreserved(t *testing.T) {
	dim := 2
	in := tb.IndexRequest{Collection: "items", Fields: []string{"embedding"}, Kind: tb.IndexVector, Dimensions: &dim}
	var calls atomic.Int32
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		w.Header().Set("Content-Type", "application/json")
		w.Header().Set("Retry-After", "0")
		w.WriteHeader(http.StatusServiceUnavailable)
		fmt.Fprint(w, `{"error":{"code":"unavailable","message":"busy"}}`)
	}))
	defer server.Close()
	c, err := tb.New(tb.Config{BaseURL: server.URL, Token: "header.project.signature", HTTPClient: server.Client()})
	if err != nil {
		t.Fatal(err)
	}
	db := c.Database(parityProject, "main")
	if _, err := db.CreateIndex(t.Context(), in); err == nil {
		t.Fatal("accepted unavailable response")
	}
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	if _, err := db.CreateIndex(ctx, in); !errors.Is(err, context.Canceled) {
		t.Fatalf("cancellation = %v", err)
	}
	if got := calls.Load(); got != 1 {
		t.Fatalf("requests = %d; want 1", got)
	}
}
