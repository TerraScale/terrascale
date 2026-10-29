package v1_test

import (
	"encoding/json"
	"errors"
	"net/http"
	"strings"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

func TestSearchQueryContracts(t *testing.T) {
	type pointer string
	for _, tc := range []struct {
		name   string
		filter tb.Filter
		wire   string
	}{
		{"pointer", tb.Search("/article/title", "quick fox"), `{"search":["/article/title","quick fox"]}`},
		{"named pointer", tb.Search(pointer("/article/title"), "quick fox"), `{"search":["/article/title","quick fox"]}`},
		{"combined", tb.And(tb.Eq("/published", true), tb.Search("/article/title", "quick fox")), `{"and":[{"eq":["/published",true]},{"search":["/article/title","quick fox"]}]}`},
		{"nested", tb.Or(tb.Not(tb.Search("/article/title", "quick fox"))), `{"or":[{"not":{"search":["/article/title","quick fox"]}}]}`},
	} {
		t.Run(tc.name, func(t *testing.T) {
			body := `{"filter":` + tc.wire + `,"sort":[{"path":"$score","order":"desc"}]}`
			reply := `{"items":[{"id":"` + parityID + `","version":"opaque","state":"value","document":{},"score":1.25},{"id":"` + parityID + `","version":"opaque","state":"value","document":{},"score":0},{"id":"` + parityID + `","version":"opaque","state":"value","document":{}}]}`
			col := parityClient(t, http.MethodPost, parityDatabase+"/collections/articles/query", "", body, reply, http.StatusOK).Database(parityProject, "main").Collection("articles")
			out, err := col.Query(t.Context(), tb.Query{Filter: tc.filter, Sort: []tb.SortKey{tb.Desc(pointer(tb.ScorePath))}})
			if err != nil {
				t.Fatal(err)
			}
			if len(out.Items) != 3 || out.Items[0].Score == nil || *out.Items[0].Score != 1.25 || out.Items[1].Score == nil || *out.Items[1].Score != 0 || out.Items[2].Score != nil {
				t.Fatalf("search scores = %+v", out.Items)
			}
		})
	}
}

func TestCombinedSearchRetainsCallerFilters(t *testing.T) {
	for _, tc := range []struct {
		name    string
		combine func(...tb.Filter) tb.Filter
	}{
		{"and", tb.And},
		{"or", tb.Or},
	} {
		t.Run(tc.name, func(t *testing.T) {
			filters := []tb.Filter{tb.Search("/title", "fox"), tb.Eq("/published", true)}
			filter := tc.combine(filters...)
			filters[0] = tb.Search("/title", "dog")
			filters[1] = tb.Search("/body", "dog")
			body := `{"filter":{"` + tc.name + `":[{"search":["/title","fox"]},{"eq":["/published",true]}]},"sort":[{"path":"$score","order":"desc"}]}`
			col := parityClient(t, http.MethodPost, parityDatabase+"/collections/articles/query", "", body, `{"items":[]}`, http.StatusOK).Database(parityProject, "main").Collection("articles")
			if _, err := col.Query(t.Context(), tb.Query{Filter: filter, Sort: []tb.SortKey{tb.Desc(tb.ScorePath)}}); err != nil {
				t.Fatal(err)
			}
		})
	}
}

func TestSearchRejectsInvalidPathsAndScoreSorts(t *testing.T) {
	c, err := tb.New(tb.Config{BaseURL: "https://tb.example", Token: "header.project.signature", HTTPClient: &http.Client{Transport: failTransport{t}}})
	if err != nil {
		t.Fatal(err)
	}
	col := c.Database(parityProject, "main").Collection("articles")
	for name, q := range map[string]tb.Query{
		"id search":        {Filter: tb.Search(tb.IDPath, "fox")},
		"score search":     {Filter: tb.Search(tb.ScorePath, "fox")},
		"nonpointer":       {Filter: tb.Search("title", "fox")},
		"empty path":       {Filter: tb.Search("", "fox")},
		"empty terms":      {Filter: tb.Search("/title", "")},
		"long terms":       {Filter: tb.Search("/title", strings.Repeat("x", 4097))},
		"unicode bytes":    {Filter: tb.Search("/title", strings.Repeat("🦊", 1025))},
		"invalid UTF-8":    {Filter: tb.Search("/title", "quick\xfffox")},
		"NUL terms":        {Filter: tb.Search("/title", "quick\x00fox")},
		"score only":       {Sort: []tb.SortKey{tb.Desc(tb.ScorePath)}},
		"score unrelated":  {Filter: tb.Eq("/title", "fox"), Sort: []tb.SortKey{tb.Desc(tb.ScorePath)}},
		"score ascending":  {Filter: tb.Search("/title", "fox"), Sort: []tb.SortKey{tb.Asc(tb.ScorePath)}},
		"score second key": {Filter: tb.Search("/title", "fox"), Sort: []tb.SortKey{tb.Desc(tb.ScorePath), tb.Asc(tb.IDPath)}},
		"two searches":     {Filter: tb.And(tb.Search("/title", "fox"), tb.Search("/body", "dog")), Sort: []tb.SortKey{tb.Desc(tb.ScorePath)}},
		"search near":      {Filter: tb.Search("/title", "fox"), Sort: []tb.SortKey{tb.Near("/embedding", []float32{1}, tb.Cosine)}},
	} {
		t.Run(name, func(t *testing.T) {
			if _, err := col.Query(t.Context(), q); err == nil || (!strings.Contains(err.Error(), "search") && !strings.Contains(err.Error(), "$score")) {
				t.Fatalf("invalid query error = %v", err)
			}
		})
	}
	for _, terms := range []string{strings.Repeat("x", 4096), strings.Repeat("🦊", 1024)} {
		if _, err := json.Marshal(tb.Search("/title", terms)); err != nil {
			t.Fatalf("valid term byte limit: %v", err)
		}
	}
}

func TestSearchCursorAndMissingIndex(t *testing.T) {
	t.Run("cursor", func(t *testing.T) {
		const body = `{"filter":{"search":["/title","fox"]},"sort":[{"path":"$score","order":"desc"}],"cursor":"score-id"}`
		col := parityClient(t, http.MethodPost, parityDatabase+"/collections/articles/query", "", body, `{"items":[],"next_cursor":"next-score-id"}`, http.StatusOK).Database(parityProject, "main").Collection("articles")
		out, err := col.Query(t.Context(), tb.Query{Filter: tb.Search("/title", "fox"), Sort: []tb.SortKey{tb.Desc(tb.ScorePath)}, Cursor: "score-id"})
		if err != nil || out.NextCursor != "next-score-id" {
			t.Fatalf("search cursor = %+v, %v", out, err)
		}
	})
	t.Run("missing index", func(t *testing.T) {
		col := parityClient(t, http.MethodPost, parityDatabase+"/collections/articles/query", "", `{"filter":{"search":["/title","fox"]}}`, `{"error":{"code":"invalid_query","message":"text index required"}}`, http.StatusBadRequest).Database(parityProject, "main").Collection("articles")
		_, err := col.Query(t.Context(), tb.Query{Filter: tb.Search("/title", "fox")})
		if !errors.Is(err, tb.ErrInvalidQuery) {
			t.Fatalf("missing index = %v", err)
		}
	})
}

func TestTextIndexContracts(t *testing.T) {
	const index = `{"name":"article_text","collection":"articles","fields":["title","article.body"],"state":"active","kind":"text"}`
	t.Run("create", func(t *testing.T) {
		const body = `{"collection":"articles","fields":["title","article.body"],"name":"article_text","kind":"text"}`
		db := parityClient(t, http.MethodPost, parityDatabase+"/indexes", "", body, index, http.StatusCreated).Database(parityProject, "main")
		out, err := db.CreateIndex(t.Context(), tb.IndexRequest{Collection: "articles", Fields: []string{"title", "article.body"}, Name: "article_text", Kind: tb.IndexText})
		if err != nil || out.Kind != tb.IndexText {
			t.Fatalf("text index = %+v, %v", out, err)
		}
	})
	t.Run("list", func(t *testing.T) {
		db := parityClient(t, http.MethodGet, parityDatabase+"/indexes", "collection=articles", "", `{"items":[`+index+`]}`, http.StatusOK).Database(parityProject, "main")
		out, err := db.Indexes(t.Context(), tb.IndexListOptions{Collection: "articles"})
		if err != nil || len(out) != 1 || out[0].Kind != tb.IndexText {
			t.Fatalf("text indexes = %+v, %v", out, err)
		}
	})
	t.Run("btree explicit", func(t *testing.T) {
		raw, err := json.Marshal(tb.IndexRequest{Collection: "articles", Fields: []string{"title"}, Kind: tb.IndexBTree})
		if err != nil || string(raw) != `{"collection":"articles","fields":["title"],"kind":"btree"}` {
			t.Fatalf("btree request = %s, %v", raw, err)
		}
	})
}
