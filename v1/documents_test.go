package v1_test

// Fast-suite integration tests for the v1 Documents API over HTTPS against
// the contract fixture (fixture_test.go).

import (
	"encoding/json"
	"errors"
	"net/http"
	"strings"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

func newClient(t *testing.T, f *fixture, token string, mutate ...func(*v1.Config)) *v1.Client {
	t.Helper()
	cfg := v1.Config{BaseURL: f.origin.URL, Token: token, HTTPClient: f.httpClient()}
	for _, m := range mutate {
		m(&cfg)
	}
	c, err := v1.New(cfg)
	if err != nil {
		t.Fatal(err)
	}
	return c
}

func requireCode(t *testing.T, err error, code v1.ErrorCode) *v1.APIError {
	t.Helper()
	var apiErr *v1.APIError
	if !errors.Is(err, code) || !errors.As(err, &apiErr) {
		t.Fatalf("want %s, got %v", code, err)
	}
	return apiErr
}

func TestDocumentLifecycle(t *testing.T) {
	t.Parallel()
	f := newFixture(t)
	ctx := t.Context()
	users := newClient(t, f, f.dataToken).Database("main").Collection("users")

	// Exact numbers survive the round trip untouched.
	const big = `12345678901234567890.000000000000000001`
	created, err := users.Create(ctx, json.RawMessage(`{"email":"Ana@Example.com","n":`+big+`,"logins":1,"legacy":true}`))
	if err != nil {
		t.Fatal(err)
	}
	if !created.Created || created.ID[14] != '7' || created.Location != "/v1/databases/main/collections/users/documents/"+created.ID ||
		created.Offset == 0 || created.Durability != v1.LocalDurable || !strings.HasPrefix(created.Version, "tbv1-") {
		t.Fatalf("create result: %+v", created)
	}
	doc, err := users.Get(ctx, created.ID)
	if err != nil {
		t.Fatal(err)
	}
	var body map[string]any
	if err := doc.Decode(&body); err != nil {
		t.Fatal(err)
	}
	if n, ok := body["n"].(json.Number); !ok || n.String() != big || doc.State != v1.StateValue || doc.Version != created.Version {
		t.Fatalf("decoded %#v (%T)", body["n"], body["n"])
	}
	if _, err := users.Get(ctx, created.ID, v1.IfNoneMatch(doc.Version)); !errors.Is(err, v1.ErrNotModified) {
		t.Fatalf("If-None-Match current version: %v", err)
	}

	steps := []struct {
		name  string
		write func() (*v1.WriteResult, error)
		want  v1.ErrorCode
	}{
		{"put with stale If-Match", func() (*v1.WriteResult, error) {
			return users.Put(ctx, created.ID, map[string]any{"email": "x@y.z"}, v1.IfMatch("tbv1-00000000000000000000000000000000"))
		}, v1.ErrPreconditionFailed},
		{"put If-None-Match * on an existing document", func() (*v1.WriteResult, error) {
			return users.Put(ctx, created.ID, map[string]any{"email": "x@y.z"}, v1.IfNoneMatchAny())
		}, v1.ErrPreconditionFailed},
		{"merge patch removes null members", func() (*v1.WriteResult, error) {
			return users.Patch(ctx, created.ID, v1.MergePatch(map[string]any{"legacy": nil, "profile": map[string]any{"name": "Ana"}}), v1.IfMatch(doc.Version))
		}, ""},
		{"ops patch with exact inc", func() (*v1.WriteResult, error) {
			return users.Patch(ctx, created.ID, v1.Ops(v1.Set("/profile/city", "Recife"), v1.Inc("/logins", 41), v1.Inc("/n", json.Number("0.000000000000000009")),
				v1.Append("/tags", "beta"), v1.Unset("/profile/name")))
		}, ""},
		{"inc on a non-number", func() (*v1.WriteResult, error) {
			return users.Patch(ctx, created.ID, v1.Ops(v1.Inc("/email", 1)))
		}, v1.ErrPatchConflict},
		{"patch of a missing document", func() (*v1.WriteResult, error) {
			return users.Patch(ctx, v1.DocumentIDFor("users", "nobody"), v1.MergePatch(map[string]any{"a": 1}))
		}, v1.ErrNotFound},
		{"unique violation is case-folded", func() (*v1.WriteResult, error) {
			return users.Create(ctx, map[string]any{"email": "ana@example.COM"})
		}, v1.ErrUniqueViolation},
	}
	for _, step := range steps {
		res, err := step.write()
		if step.want == "" {
			if err != nil || res.Created {
				t.Fatalf("%s: %+v %v", step.name, res, err)
			}
			continue
		}
		apiErr := requireCode(t, err, step.want)
		if step.want == v1.ErrUniqueViolation && apiErr.Constraint != "email" {
			t.Fatalf("%s: constraint %q", step.name, apiErr.Constraint)
		}
	}
	doc, err = users.Get(ctx, created.ID)
	if err != nil {
		t.Fatal(err)
	}
	if got := string(doc.Document); got != `{"email":"Ana@Example.com","logins":42,"n":12345678901234567890.00000000000000001,"profile":{"city":"Recife"},"tags":["beta"]}` {
		t.Fatalf("patched document: %s", got)
	}

	// Delete with If-Match; the 404 carries the tombstone version, which
	// recreates the document with If-Match.
	if _, err := users.Delete(ctx, created.ID, v1.IfMatch(created.Version)); !errors.Is(err, v1.ErrPreconditionFailed) {
		t.Fatalf("stale delete: %v", err)
	}
	deleted, err := users.Delete(ctx, created.ID, v1.IfMatch(doc.Version))
	if err != nil {
		t.Fatal(err)
	}
	_, err = users.Get(ctx, created.ID)
	if apiErr := requireCode(t, err, v1.ErrNotFound); apiErr.ETag != deleted.Version {
		t.Fatalf("404 ETag %q, want tombstone %q", apiErr.ETag, deleted.Version)
	}
	if _, err := users.Delete(ctx, created.ID); !errors.Is(err, v1.ErrNotFound) {
		t.Fatalf("second delete: %v", err)
	}
	recreated, err := users.Put(ctx, created.ID, map[string]any{"email": "ana@example.com"}, v1.IfMatch(deleted.Version))
	if err != nil || !recreated.Created || recreated.Offset <= deleted.Offset {
		t.Fatalf("recreate: %+v %v", recreated, err)
	}
}

func TestNaturalKeyIDsAndConflicts(t *testing.T) {
	t.Parallel()
	f := newFixture(t)
	ctx := t.Context()
	db := newClient(t, f, f.dataToken).Database("main")
	orgs := db.Collection("orgs")

	// uuidv5(uuidv5(NAMESPACE_URL, "terrabase:collection:orgs"), "acme"),
	// computed independently with Python's uuid module.
	const acme = "01a1fce3-a744-5f00-a4fc-63761af07647"
	res, err := orgs.Put(ctx, v1.DocumentIDFor("orgs", "acme"), map[string]any{"name": "Acme"}, v1.IfNoneMatchAny())
	if err != nil || res.ID != acme || !res.Created {
		t.Fatalf("natural key put: %+v %v", res, err)
	}
	if v1.DocumentIDFor("users", "ana@example.com") != "b85e5c40-9a0a-517f-ba69-779a0cd2b159" {
		t.Fatal("users namespace vector")
	}
	// Reads are replay-safe, so a transient 503 on GET is retried.
	f.inject(fxFault{method: "GET", pathPrefix: "/v1/databases/main/collections/orgs/documents/", status: 503, code: "storage_unavailable"})
	if _, err := orgs.Get(ctx, acme); err != nil || f.faultsLeft() != 0 {
		t.Fatalf("retried read: %v", err)
	}

	version := f.seedConflict("main", "orgs", acme, `{"name":"Acme A"}`, `{"name":"Acme B"}`)
	doc, err := orgs.Get(ctx, acme)
	if err != nil {
		t.Fatal(err)
	}
	if ev := doc.Siblings[1].Event; doc.State != v1.StateConflict || len(doc.Siblings) != 2 || ev.Sequence != 14 || doc.Version != version ||
		len(ev.Replica) != 36 || len(ev.Incarnation) != 32 {
		t.Fatalf("conflict: %+v", doc)
	}
	if _, err := orgs.Patch(ctx, acme, v1.MergePatch(map[string]any{"x": 1})); !errors.Is(err, v1.ErrConflictUnresolved) {
		t.Fatalf("patch conflicted: %v", err)
	}
	if _, err := orgs.Put(ctx, acme, map[string]any{"name": "Acme"}, v1.IfMatch(doc.Version)); err != nil {
		t.Fatalf("resolving put: %v", err)
	}
	if doc, err := orgs.Get(ctx, acme); err != nil || doc.State != v1.StateValue {
		t.Fatalf("resolved: %+v %v", doc, err)
	}
}

func TestRetriesAndIdempotency(t *testing.T) {
	t.Parallel()
	const prefix = "/v1/databases/main/collections/items/documents"
	cases := []struct {
		name       string
		fault      fxFault
		key        string
		maxRetries int
		want       v1.ErrorCode
		requests   int
	}{
		{"429 is retried after Retry-After", fxFault{method: "POST", status: 429, code: "rate_limited", retryAfter: "0"}, "", 0, "", 2},
		{"429 retries are bounded", fxFault{method: "POST", status: 429, code: "rate_limited", retryAfter: "0", remaining: 5}, "", 2, v1.ErrRateLimited, 3},
		{"commit_uncertain with a key replays the original", fxFault{method: "POST", status: 503, code: "commit_uncertain", afterCommit: true}, "create-1", 0, "", 2},
		{"commit_uncertain without a key is not retried", fxFault{method: "POST", status: 503, code: "commit_uncertain", afterCommit: true}, "", 0, v1.ErrCommitUncertain, 1},
		{"transient 503 with a key is retried", fxFault{method: "POST", status: 503, code: "capacity"}, "k", 0, "", 2},
		{"dependencies_unavailable with a key is retried", fxFault{method: "POST", status: 503, code: "dependencies_unavailable", retryAfter: "0"}, "k", 0, "", 2},
		{"transient 503 without a key is not retried", fxFault{method: "POST", status: 503, code: "storage_unavailable"}, "", 0, v1.ErrStorageUnavailable, 1},
		{"stored_corruption is never retried", fxFault{method: "POST", status: 503, code: "stored_corruption"}, "k", 0, v1.ErrStoredCorruption, 1},
		{"internal_error with a key replays the original", fxFault{method: "POST", status: 500, code: "internal_error", afterCommit: true}, "create-1", 0, "", 2},
		{"internal_error without a key is not retried", fxFault{method: "POST", status: 500, code: "internal_error"}, "", 0, v1.ErrInternalError, 1},
		{"auto key retries commit_uncertain", fxFault{method: "POST", status: 503, code: "commit_uncertain", afterCommit: true}, "auto", 0, "", 2},
		{"auto key retries internal_error", fxFault{method: "POST", status: 500, code: "internal_error", afterCommit: true}, "auto", 0, "", 2},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()
			f := newFixture(t)
			items := newClient(t, f, f.dataToken, func(c *v1.Config) {
				c.MaxRetries = tc.maxRetries
				c.AutoIdempotencyKeys = tc.key == "auto"
			}).Database("main").Collection("items")
			tc.fault.pathPrefix = prefix
			f.inject(tc.fault)
			var opts []v1.WriteOption
			if tc.key != "" && tc.key != "auto" {
				opts = append(opts, v1.IdempotencyKey(tc.key))
			}
			res, err := items.Create(t.Context(), map[string]any{"n": 1}, opts...)
			if got := f.count("POST", prefix); got != tc.requests {
				t.Fatalf("requests = %d, want %d", got, tc.requests)
			}
			if tc.want != "" {
				apiErr := requireCode(t, err, tc.want)
				if tc.want == v1.ErrRateLimited && apiErr.RetryAfter != 0 {
					t.Fatalf("RetryAfter %v", apiErr.RetryAfter)
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			if tc.key == "create-1" {
				// The retry returned the original commit: one write, same ID.
				again, err := items.Create(t.Context(), map[string]any{"n": 1}, opts...)
				if err != nil || again.ID != res.ID || again.Offset != res.Offset || again.Offset != 1 {
					t.Fatalf("replay: %+v vs %+v (%v)", again, res, err)
				}
				_, err = items.Create(t.Context(), map[string]any{"n": 2}, opts...)
				requireCode(t, err, v1.ErrIdempotencyConflict)
			}
			if tc.key == "auto" {
				// The retry replayed the first commit instead of writing twice.
				page, err := items.Query(t.Context(), v1.Query{Filter: v1.Eq("/n", 1)})
				if err != nil || len(page.Items) != 1 || page.Items[0].ID != res.ID || res.Offset != 1 {
					t.Fatalf("auto key: %+v, offset %d (%v)", page, res.Offset, err)
				}
			}
		})
	}
}

func TestAutoIdempotencyKeys(t *testing.T) {
	t.Parallel()
	f := newFixture(t)
	ctx := t.Context()
	c := newClient(t, f, f.dataToken, func(c *v1.Config) { c.AutoIdempotencyKeys = true; c.MaxRetries = -1 })
	items := c.Database("main").Collection("items")

	// Every keyless write gets its own key: two creates are two documents.
	a, err := items.Create(ctx, map[string]any{"n": 1})
	if err != nil {
		t.Fatal(err)
	}
	b, err := items.Create(ctx, map[string]any{"n": 1})
	if err != nil || a.ID == b.ID {
		t.Fatalf("distinct keys expected: %v %v (%v)", a, b, err)
	}

	// When retries run out (here they are disabled), the generated key is
	// reported so the caller can retry the same write and learn its original
	// outcome.
	const prefix = "/v1/databases/main/transactions"
	f.inject(fxFault{method: "POST", pathPrefix: prefix, status: 503, code: "commit_uncertain", afterCommit: true})
	id := v1.DocumentIDFor("items", "tx")
	tx := func() *v1.Tx { return c.Database("main").Tx().Put("items", id, map[string]any{"n": 7}) }
	_, err = tx().Commit(ctx)
	apiErr := requireCode(t, err, v1.ErrCommitUncertain)
	if apiErr.IdempotencyKey == "" || f.count("POST", prefix) != 1 {
		t.Fatalf("key %q after %d requests", apiErr.IdempotencyKey, f.count("POST", prefix))
	}
	res, err := tx().Commit(ctx, v1.IdempotencyKey(apiErr.IdempotencyKey))
	if err != nil {
		t.Fatal(err)
	}
	doc, err := items.Get(ctx, id)
	if err != nil || doc.Version != res.Results[0].Version || f.count("POST", prefix) != 2 {
		t.Fatalf("replayed commit: %+v vs %+v (%v)", doc, res, err)
	}

	// An explicit key wins over the generated one.
	if _, err := items.Put(ctx, id, map[string]any{"n": 8}, v1.IdempotencyKey("explicit-1")); err != nil {
		t.Fatal(err)
	}
	_, err = items.Put(ctx, id, map[string]any{"n": 9}, v1.IdempotencyKey("explicit-1"))
	requireCode(t, err, v1.ErrIdempotencyConflict)
}

func TestAuthenticationAndScope(t *testing.T) {
	t.Parallel()
	f := newFixture(t)
	ctx := t.Context()
	id := v1.DocumentIDFor("users", "x")
	cases := []struct {
		name  string
		token string
		db    string
		want  v1.ErrorCode
	}{
		{"unknown key", "tbk1.AAAAAAAAAAAAAAAAAAAAAA.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "main", v1.ErrInvalidKey},
		{"control key cannot read documents", f.controlToken, "main", v1.ErrForbiddenScope},
		{"wildcard grant learns unknown databases", f.dataToken, "nope", v1.ErrUnknownDatabase},
	}
	for _, tc := range cases {
		_, err := newClient(t, f, tc.token).Database(tc.db).Collection("users").Get(ctx, id)
		if apiErr := requireCode(t, err, tc.want); apiErr.Status == http.StatusOK {
			t.Fatal(tc.name)
		}
	}
	// Invalid names and IDs never reach the server.
	c := newClient(t, f, f.dataToken)
	for _, err := range []error{
		func() error { _, err := c.Database("Main").Collection("users").Get(ctx, id); return err }(),
		func() error { _, err := c.Database("main").Collection("users").Get(ctx, "../x"); return err }(),
		func() error { _, err := c.Database("main").Collection("users").Create(ctx, []int{1}); return err }(),
	} {
		var apiErr *v1.APIError
		if err == nil || errors.As(err, &apiErr) {
			t.Fatalf("want a local validation error, got %v", err)
		}
	}
	if _, err := v1.New(v1.Config{BaseURL: "http://example.com", Token: f.dataToken}); err == nil {
		t.Fatal("plain HTTP to a non-loopback host must be rejected")
	}
}
