package v1_test

// Fast-suite integration tests for transactions and queries against the
// contract fixture (fixture_test.go).

import (
	"encoding/json"
	"errors"
	"fmt"
	"slices"
	"strings"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

func TestTransactions(t *testing.T) {
	t.Parallel()
	f := newFixture(t)
	ctx := t.Context()
	db := newClient(t, f, f.dataToken).Database("main")
	id := func(coll, key string) string { return v1.DocumentIDFor(coll, key) }

	seed, err := db.Tx().
		Put("projects", id("projects", "p1"), map[string]any{"db_count": 1}).
		Put("sessions", id("sessions", "s1"), map[string]any{"user": "ana"}).
		Put("plans", id("plans", "free"), map[string]any{"visible": true}).
		Put("users", id("users", "ana"), map[string]any{"email": "ana@example.com"}).
		Commit(ctx)
	if err != nil || len(seed.Results) != 4 {
		t.Fatalf("seed: %+v %v", seed, err)
	}
	projectVersion, sessionVersion := seed.Results[0].Version, seed.Results[1].Version

	res, err := db.Tx().
		Create("users", "", map[string]any{"email": "bia@example.com"}).
		Put("orgs", id("orgs", "acme"), map[string]any{"name": "Acme"}, v1.IfNoneMatchAny()).
		Patch("projects", id("projects", "p1"), v1.Ops(v1.Inc("/db_count", 1)), v1.IfMatch(projectVersion)).
		Patch("users", id("users", "ana"), v1.MergePatch(map[string]any{"name": "Ana"})).
		Delete("sessions", id("sessions", "s1"), v1.IfMatch(sessionVersion)).
		Assert("plans", id("plans", "free"), v1.Eq("/visible", true)).
		AssertAbsent("invites", id("invites", "x")).
		Commit(ctx, v1.IdempotencyKey("tx-1"))
	if err != nil {
		t.Fatal(err)
	}
	if res.Offset != seed.Offset+1 || res.Durability != v1.LocalDurable || len(res.Results) != 7 ||
		res.Results[0].ID == "" || res.Results[1].ID != id("orgs", "acme") || !res.Results[5].Asserted || !res.Results[6].Asserted {
		t.Fatalf("tx result: %+v", res)
	}
	project, err := db.Collection("projects").Get(ctx, id("projects", "p1"))
	if err != nil || string(project.Document) != `{"db_count":2}` {
		t.Fatalf("project after tx: %s %v", project.Document, err)
	}

	failures := []struct {
		name       string
		tx         *v1.Tx
		code       v1.ErrorCode
		op         int
		constraint string
	}{
		{"failed assert", db.Tx().
			Put("items", id("items", "a"), map[string]any{"a": 1}).
			Assert("plans", id("plans", "free"), v1.Eq("/visible", false)), v1.ErrPreconditionFailed, 1, ""},
		{"failed AssertAbsent", db.Tx().
			Put("items", id("items", "a"), map[string]any{"a": 1}).
			AssertAbsent("orgs", id("orgs", "acme")), v1.ErrPreconditionFailed, 1, ""},
		{"unique violation", db.Tx().
			Put("items", id("items", "a"), map[string]any{"a": 1}).
			Put("items", id("items", "b"), map[string]any{"a": 1}).
			Create("users", "", map[string]any{"email": "ANA@example.com"}), v1.ErrUniqueViolation, 2, "email"},
		{"patch of a missing document", db.Tx().
			Put("items", id("items", "a"), map[string]any{"a": 1}).
			Patch("items", id("items", "missing"), v1.MergePatch(map[string]any{"a": 2})), v1.ErrNotFound, 1, ""},
		{"stale If-Match", db.Tx().
			Delete("projects", id("projects", "p1"), v1.IfMatch(projectVersion)), v1.ErrPreconditionFailed, 0, ""},
	}
	for _, tc := range failures {
		_, err := tc.tx.Commit(ctx)
		apiErr := requireCode(t, err, tc.code)
		if op, ok := apiErr.OpIndex(); !ok || op != tc.op || apiErr.Constraint != tc.constraint {
			t.Fatalf("%s: op %v constraint %q", tc.name, apiErr.Op, apiErr.Constraint)
		}
	}
	// Failed transactions are atomic: their earlier ops were not applied.
	if _, err := db.Collection("items").Get(ctx, id("items", "a")); !errors.Is(err, v1.ErrNotFound) {
		t.Fatalf("aborted write is visible: %v", err)
	}
	// Duplicate targets and preconditions on create are rejected by op index.
	for _, tx := range []*v1.Tx{
		db.Tx().Put("items", id("items", "d"), map[string]any{}).Delete("items", id("items", "d")),
	} {
		_, err := tx.Commit(ctx)
		if op, ok := requireCode(t, err, v1.ErrInvalidRequest).OpIndex(); !ok || op != 1 {
			t.Fatalf("duplicate target op = %v", op)
		}
	}
	// Builder errors are reported locally with the op index.
	_, err = db.Tx().Put("items", "not-a-uuid", map[string]any{}).Commit(ctx)
	if err == nil || !strings.Contains(err.Error(), "op 0") {
		t.Fatalf("local builder error: %v", err)
	}
	// If-None-Match: * only applies to put; patch and delete reject it.
	_, err = db.Tx().
		Put("items", id("items", "x"), map[string]any{}, v1.IfNoneMatchAny()).
		Patch("items", id("items", "y"), v1.MergePatch(map[string]any{"a": 1}), v1.IfNoneMatchAny()).
		Delete("items", id("items", "z"), v1.IfNoneMatchAny()).
		Commit(ctx)
	if err == nil || !strings.Contains(err.Error(), "op 1: patch does not take IfNoneMatchAny") || !strings.Contains(err.Error(), "op 2: delete does not take IfNoneMatchAny") {
		t.Fatalf("IfNoneMatchAny on patch/delete: %v", err)
	}
}

func TestQueries(t *testing.T) {
	t.Parallel()
	f := newFixture(t)
	ctx := t.Context()
	items := newClient(t, f, f.dataToken).Database("main").Collection("items")

	seed := []string{
		`{"name":"Alpha","n":1,"tags":["a","b"],"org":"o1"}`,
		`{"name":"alpine","n":10.5,"tags":["b"],"org":"o1"}`,
		`{"name":"Beta","n":100000000000000000000000000001,"org":"o2","extra":null}`,
		`{"name":"gamma","n":"7","org":"o2"}`,
		`{"name":"Delta","n":-3,"org":"o3","tags":[]}`,
	}
	ids := make([]string, len(seed))
	for i, doc := range seed {
		ids[i] = v1.DocumentIDFor("items", fmt.Sprint(i))
		if _, err := items.Put(ctx, ids[i], json.RawMessage(doc)); err != nil {
			t.Fatal(err)
		}
	}
	idx := func(want ...int) []string {
		out := make([]string, 0, len(want))
		for _, i := range want {
			out = append(out, ids[i])
		}
		slices.Sort(out)
		return out
	}
	filters := []struct {
		name   string
		filter v1.Filter
		want   []string
	}{
		{"eq exact decimal", v1.Eq("/n", json.Number("10.50")), idx(1)},
		{"eq huge integer", v1.Eq("/n", json.Number("100000000000000000000000000001")), idx(2)},
		{"gt compares numbers only", v1.Gt("/n", 5), idx(1, 2)},
		{"lte", v1.Lte("/n", 1), idx(0, 4)},
		{"lt strings", v1.Lt("/name", "B"), idx(0)},
		{"gte", v1.Gte("/n", json.Number("10.5")), idx(1, 2)},
		{"ne matches missing", v1.Ne("/extra", nil), idx(0, 1, 3, 4)},
		{"in", v1.In("/org", "o1", "o3"), idx(0, 1, 4)},
		{"nin", v1.Nin("/org", "o1"), idx(2, 3, 4)},
		{"exists includes null", v1.Exists("/extra"), idx(2)},
		{"prefix", v1.Prefix("/name", "Al"), idx(0)},
		{"prefix lower", v1.Prefix(v1.Lower("/name"), "al"), idx(0, 1)},
		{"eq lower", v1.Eq(v1.Lower("/name"), "BETA"), idx(2)},
		{"contains", v1.Contains("/tags", "b"), idx(0, 1)},
		{"id path", v1.Eq(v1.IDPath, ids[3]), idx(3)},
		{"id in", v1.In(v1.IDPath, ids[0], ids[4]), idx(0, 4)},
		{"and/or/not", v1.And(v1.Or(v1.Eq("/org", "o1"), v1.Eq("/org", "o2")), v1.Not(v1.Exists("/tags"))), idx(2, 3)},
	}
	for _, tc := range filters {
		var got []string
		for doc, err := range items.Items(ctx, v1.Query{Filter: tc.filter}) {
			if err != nil {
				t.Fatalf("%s: %v", tc.name, err)
			}
			got = append(got, doc.ID)
		}
		slices.Sort(got)
		if !slices.Equal(got, tc.want) {
			t.Fatalf("%s: got %v want %v", tc.name, got, tc.want)
		}
	}

	// Sorted, projected pages with an exact count: every match once, in order.
	q := v1.Query{Filter: v1.Exists("/org"), Sort: []v1.SortKey{v1.Asc("/org"), v1.Desc(v1.Lower("/name"))},
		Project: []string{"/name"}, Limit: 2, Count: true}
	var names []string
	pages := 0
	for page, err := range items.Pages(ctx, q) {
		if err != nil {
			t.Fatal(err)
		}
		pages++
		// count is evaluated on the first page only (not with a cursor).
		if (pages == 1) != (page.Count != nil) || (page.Count != nil && (page.Count.Value != 5 || !page.Count.Exact)) {
			t.Fatalf("page %d count: %+v", pages, page.Count)
		}
		for _, d := range page.Items {
			var body map[string]any
			if err := d.Decode(&body); err != nil || len(body) != 1 {
				t.Fatalf("projection: %s", d.Document)
			}
			names = append(names, body["name"].(string))
		}
	}
	if want := []string{"alpine", "Alpha", "gamma", "Beta", "Delta"}; pages != 3 || !slices.Equal(names, want) {
		t.Fatalf("pages %d names %v", pages, names)
	}

	first, err := items.Query(ctx, q)
	if err != nil || first.NextCursor == "" {
		t.Fatalf("first page: %+v %v", first, err)
	}
	// The cursor binds filter, sort and project; limit may change.
	wider := q
	wider.Limit, wider.Cursor = 10, first.NextCursor
	rest, err := items.Query(ctx, wider)
	if err != nil || len(rest.Items) != 3 || rest.NextCursor != "" || rest.Count != nil {
		t.Fatalf("cursor with a new limit: %+v %v", rest, err)
	}
	other := q
	other.Filter, other.Cursor = v1.Exists("/name"), first.NextCursor
	_, err = items.Query(ctx, other)
	requireCode(t, err, v1.ErrInvalidCursor)

	plan, err := items.Query(ctx, v1.Query{Filter: v1.Eq("/org", "o1"), Explain: v1.ExplainPlan})
	if err != nil || len(plan.Items) != 0 || !strings.Contains(string(plan.Explain), `"plan"`) {
		t.Fatalf("explain plan: %+v %v", plan, err)
	}
	analyze, err := items.Query(ctx, v1.Query{Filter: v1.Eq("/org", "o1"), Explain: v1.ExplainAnalyze})
	if err != nil || len(analyze.Items) != 2 || !strings.Contains(string(analyze.Explain), `"actual"`) {
		t.Fatalf("explain analyze: %+v %v", analyze, err)
	}
	if _, err := items.Query(ctx, v1.Query{Filter: v1.Eq("/tags", []string{"a"})}); err == nil {
		t.Fatal("non-scalar filter values must be rejected")
	}
	_, err = items.Query(ctx, v1.Query{Limit: 5000})
	requireCode(t, err, v1.ErrInvalidQuery)
}

func TestTransactionAssertCount(t *testing.T) {
	t.Parallel()
	f := newFixture(t)
	ctx := t.Context()
	db := newClient(t, f, f.dataToken).Database("main")
	n := func(v uint64) *uint64 { return &v }
	token := func(i int) string { return v1.DocumentIDFor("sessions", fmt.Sprint("t", i)) }
	for i := range 3 {
		if _, err := db.Collection("sessions").Put(ctx, token(i), map[string]any{"user_id": "u1"}); err != nil {
			t.Fatal(err)
		}
	}
	mine := v1.Eq("/user_id", "u1")

	// The count is taken before the transaction's own writes.
	res, err := db.Tx().
		AssertCount("sessions", mine, v1.CountBounds{Lte: n(3)}).
		Create("sessions", token(3), map[string]any{"user_id": "u1"}).
		AssertCount("sessions", mine, v1.CountBounds{Gte: n(1), Lte: n(3)}).
		Commit(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if r := res.Results; len(r) != 3 || !r[0].Asserted || r[0].Count == nil || *r[0].Count != 3 || r[2].Count == nil || *r[2].Count != 3 {
		t.Fatalf("results: %+v", res.Results)
	}

	failures := []struct {
		name string
		tx   *v1.Tx
		code v1.ErrorCode
		op   int
	}{
		{"upper bound exceeded", db.Tx().
			Create("sessions", token(9), map[string]any{"user_id": "u1"}).
			AssertCount("sessions", mine, v1.CountBounds{Lte: n(3)}), v1.ErrPreconditionFailed, 1},
		{"lower bound not met", db.Tx().
			AssertCount("sessions", v1.Eq("/user_id", "u2"), v1.CountBounds{Gte: n(1)}), v1.ErrPreconditionFailed, 0},
	}
	for _, tc := range failures {
		_, err := tc.tx.Commit(ctx)
		if op, ok := requireCode(t, err, tc.code).OpIndex(); !ok || op != tc.op {
			t.Fatalf("%s: op %d", tc.name, op)
		}
	}
	if _, err := db.Collection("sessions").Get(ctx, token(9)); !errors.Is(err, v1.ErrNotFound) {
		t.Fatalf("failed assert_count must abort the transaction: %v", err)
	}

	// Too many documents to examine without an index.
	f.SetAssertCountLimit(2)
	_, err = db.Tx().AssertCount("sessions", mine, v1.CountBounds{Lte: n(10)}).Commit(ctx)
	requireCode(t, err, v1.ErrAssertRequiresIndex)

	// Bounds and a filter are required, and at most 16 assert_count ops are
	// accepted.
	if _, err := db.Tx().AssertCount("sessions", mine, v1.CountBounds{}).Commit(ctx); err == nil || !strings.Contains(err.Error(), "Gte or Lte") {
		t.Fatalf("missing bounds: %v", err)
	}
	before := f.count("POST", "/v1/databases/main/transactions")
	if _, err := db.Tx().AssertCount("sessions", v1.Filter{}, v1.CountBounds{Lte: n(1)}).Commit(ctx); err == nil || !strings.Contains(err.Error(), "needs a filter") {
		t.Fatalf("missing filter: %v", err)
	}
	if f.count("POST", "/v1/databases/main/transactions") != before {
		t.Fatal("an invalid AssertCount reached the server")
	}
	f.SetAssertCountLimit(10000)
	many := db.Tx()
	for range 17 {
		many.AssertCount("sessions", mine, v1.CountBounds{Gte: n(0)})
	}
	_, err = many.Commit(ctx)
	if op, _ := requireCode(t, err, v1.ErrInvalidRequest).OpIndex(); op != 16 {
		t.Fatalf("17th assert_count op = %d", op)
	}
}
