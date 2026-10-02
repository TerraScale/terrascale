package v1_test

// The conformance scenario: a control key bootstraps a tenant, database,
// collections and a data key, then the data key exercises CRUD with If-Match,
// unique constraints, idempotent replay, transactions with assert_count,
// queries and a shape subscription, and finally revocation.
//
// It runs in two suites:
//
//   - fast: TestConformanceFixture runs it against the contract fixture, so
//     the scenario itself is always known to be correct;
//   - slow: TestLiveConformance runs it against a real tb-server when
//     TB_V1_URL (origin, https), TB_V1_CONTROL_KEY (a control key) and
//     TB_V1_CA_FILE (PEM bundle trusted for the origin) are set. It is skipped
//     otherwise; set TB_V1_REQUIRE=1 to fail instead of skipping.

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"errors"
	"fmt"
	"net/http"
	"os"
	"strings"
	"testing"
	"time"

	v1 "terrascale.tech/sdk/v1"
)

type conformanceTarget struct {
	baseURL, controlKey string
	hc                  *http.Client
	// revocationWait bounds how long a revoked key may keep working.
	revocationWait time.Duration
}

func TestConformanceFixture(t *testing.T) {
	t.Parallel()
	f := newFixture(t)
	runConformance(t, conformanceTarget{baseURL: f.origin.URL, controlKey: f.controlToken, hc: f.httpClient(), revocationWait: time.Second})
}

func TestLiveConformance(t *testing.T) {
	env := map[string]string{}
	var missing []string
	for _, name := range []string{"TB_V1_URL", "TB_V1_CONTROL_KEY", "TB_V1_CA_FILE"} {
		if env[name] = os.Getenv(name); env[name] == "" {
			missing = append(missing, name)
		}
	}
	if len(missing) > 0 {
		if os.Getenv("TB_V1_REQUIRE") == "1" {
			t.Fatalf("live v1 conformance preflight missing %s", strings.Join(missing, ","))
		}
		t.Skipf("live v1 conformance NOT RUN (slow suite): missing %s", strings.Join(missing, ","))
	}
	pem, err := os.ReadFile(env["TB_V1_CA_FILE"])
	if err != nil {
		t.Fatal(err)
	}
	pool := x509.NewCertPool()
	if !pool.AppendCertsFromPEM(pem) {
		t.Fatalf("TB_V1_CA_FILE %s holds no PEM certificates", env["TB_V1_CA_FILE"])
	}
	hc := &http.Client{Timeout: time.Minute, Transport: &http.Transport{
		Proxy:             http.ProxyFromEnvironment,
		TLSClientConfig:   &tls.Config{MinVersion: tls.VersionTLS13, RootCAs: pool},
		ForceAttemptHTTP2: true,
	}}
	runConformance(t, conformanceTarget{baseURL: env["TB_V1_URL"], controlKey: env["TB_V1_CONTROL_KEY"], hc: hc, revocationWait: 10 * time.Second})
}

func runConformance(t *testing.T, target conformanceTarget) {
	ctx, cancel := context.WithTimeout(t.Context(), 2*time.Minute)
	defer cancel()
	must := func(err error) {
		t.Helper()
		if err != nil {
			t.Fatal(err)
		}
	}
	dash, err := v1.NewDashboard(v1.Config{BaseURL: target.baseURL, Token: target.controlKey, HTTPClient: target.hc})
	must(err)
	key := v1.NewIdempotencyKey

	// Bootstrap: tenant → database → collections → data key.
	run := strings.ToLower(v1.NewIdempotencyKey()[:10])
	tenant, err := dash.CreateTenant(ctx, key(), v1.CreateTenantRequest{Name: "ts-go conformance " + run, ExternalRef: "tsgo-conf-" + run})
	must(err)
	t.Cleanup(func() {
		ctx, cancel := context.WithTimeout(context.Background(), time.Minute)
		defer cancel()
		op, err := dash.DeleteTenant(ctx, key(), tenant.ID)
		if err == nil {
			_, err = dash.WaitOperation(ctx, op.ID, 200*time.Millisecond)
		}
		if err != nil {
			t.Errorf("cleanup: delete tenant %s: %v", tenant.ID, err)
		}
	})
	if got, err := dash.GetTenant(ctx, tenant.ID); err != nil || got.ExternalRef != "tsgo-conf-"+run {
		t.Fatalf("get tenant: %+v %v", got, err)
	}
	regions, err := dash.ListRegions(ctx, v1.ListOptions{})
	must(err)
	var region string
	for _, r := range regions.Items {
		if r.Status == "available" {
			region = r.Code
			break
		}
	}
	dbInfo, err := dash.CreateDatabase(ctx, key(), tenant.ID, v1.CreateDatabaseRequest{Name: "main", HomeRegion: region})
	must(err)
	if dbInfo.Name != "main" || dbInfo.Status != "ready" {
		t.Fatalf("database: %+v", dbInfo)
	}
	_, err = dash.CreateCollection(ctx, key(), tenant.ID, "main", v1.CreateCollectionRequest{Name: "users",
		Unique: []v1.UniqueConstraint{{Name: "email", Paths: []string{"/email"}, Transform: "lower"}}})
	must(err)
	_, err = dash.CreateCollection(ctx, key(), tenant.ID, "main", v1.CreateCollectionRequest{Name: "sessions"})
	must(err)
	dataKey, err := dash.CreateKey(ctx, key(), tenant.ID, v1.CreateKeyRequest{Name: "conformance",
		Grants: []v1.Grant{{Database: "main", Collections: []string{"*"}, Permissions: []string{"read", "write", "shape"}}}})
	must(err)

	c, err := v1.New(v1.Config{BaseURL: target.baseURL, Token: dataKey.Token, HTTPClient: target.hc, AutoIdempotencyKeys: true})
	must(err)
	db := c.Database("main")
	users, sessions := db.Collection("users"), db.Collection("sessions")

	// CRUD with If-Match.
	ana := v1.DocumentIDFor("users", "ana")
	created, err := users.Put(ctx, ana, map[string]any{"email": "Ana@Example.com", "n": 1}, v1.IfNoneMatchAny())
	must(err)
	if !created.Created || created.Version == "" {
		t.Fatalf("create: %+v", created)
	}
	doc, err := users.Get(ctx, ana)
	must(err)
	var body map[string]any
	must(doc.Decode(&body))
	if doc.Version != created.Version || body["email"] != "Ana@Example.com" {
		t.Fatalf("get: %+v %v", doc, body)
	}
	_, err = users.Get(ctx, ana, v1.IfNoneMatch(doc.Version))
	requireCode(t, err, v1.ErrNotModified)
	patched, err := users.Patch(ctx, ana, v1.Ops(v1.Inc("/n", 41)), v1.IfMatch(doc.Version))
	must(err)
	_, err = users.Put(ctx, ana, map[string]any{"email": "ana@example.com"}, v1.IfMatch(doc.Version))
	requireCode(t, err, v1.ErrPreconditionFailed)
	if doc, err = users.Get(ctx, ana); err != nil || doc.Version != patched.Version {
		t.Fatalf("after patch: %+v %v", doc, err)
	}
	must(doc.Decode(&body))
	if fmt.Sprint(body["n"]) != "42" {
		t.Fatalf("inc: n = %v", body["n"])
	}

	// Unique constraint (lower-cased) and idempotent replay.
	_, err = users.Create(ctx, map[string]any{"email": "ANA@example.com"})
	if apiErr := requireCode(t, err, v1.ErrUniqueViolation); apiErr.Constraint != "email" {
		t.Fatalf("constraint %q", apiErr.Constraint)
	}
	idem := v1.IdempotencyKey(v1.NewIdempotencyKey())
	first, err := users.Create(ctx, map[string]any{"email": "bia@example.com"}, idem)
	must(err)
	again, err := users.Create(ctx, map[string]any{"email": "bia@example.com"}, idem)
	if err != nil || again.ID != first.ID || again.Version != first.Version {
		t.Fatalf("replay: %+v vs %+v (%v)", again, first, err)
	}
	_, err = users.Create(ctx, map[string]any{"email": "cai@example.com"}, idem)
	requireCode(t, err, v1.ErrIdempotencyConflict)

	// Transactions: assert_count is evaluated before the transaction's writes.
	n := func(v uint64) *uint64 { return &v }
	mine := v1.Eq("/user_id", ana)
	tok := func(i int) string { return v1.DocumentIDFor("sessions", fmt.Sprint(run, i)) }
	res, err := db.Tx().
		AssertCount("sessions", mine, v1.CountBounds{Lte: n(1)}).
		Create("sessions", tok(0), map[string]any{"user_id": ana}).
		Create("sessions", tok(1), map[string]any{"user_id": ana}).
		Patch("users", ana, v1.MergePatch(map[string]any{"sessions": 2}), v1.IfMatch(doc.Version)).
		Commit(ctx)
	must(err)
	if len(res.Results) != 4 || res.Results[0].Count == nil || *res.Results[0].Count != 0 {
		t.Fatalf("tx results: %+v", res.Results)
	}
	_, err = db.Tx().
		Create("sessions", tok(2), map[string]any{"user_id": ana}).
		AssertCount("sessions", mine, v1.CountBounds{Lte: n(1)}).
		Commit(ctx)
	if op, ok := requireCode(t, err, v1.ErrPreconditionFailed).OpIndex(); !ok || op != 1 {
		t.Fatalf("failing op %d", op)
	}
	if _, err := sessions.Get(ctx, tok(2)); !errors.Is(err, v1.ErrNotFound) {
		t.Fatalf("aborted transaction wrote: %v", err)
	}

	// Queries.
	page, err := sessions.Query(ctx, v1.Query{Filter: mine, Count: true})
	must(err)
	if len(page.Items) != 2 || page.Count == nil {
		t.Fatalf("query: %d items, count %+v", len(page.Items), page.Count)
	}

	// Shapes: the snapshot catches up, then a write arrives.
	sub, err := db.Subscribe(ctx, v1.ShapeRequest{Collection: "sessions", Filter: mine})
	must(err)
	defer sub.Close()
	seen := map[string]bool{}
	next := func() v1.ShapeEvent {
		t.Helper()
		ev, err := sub.Next(ctx)
		must(err)
		for _, ch := range ev.Changes {
			for _, op := range ch.Ops {
				seen[op.ID] = op.Op == v1.OpUpsert
			}
		}
		return ev
	}
	for ev := next(); !ev.UpToDate; ev = next() {
	}
	if !seen[tok(0)] || !seen[tok(1)] {
		t.Fatalf("snapshot: %v", seen)
	}
	w, err := sessions.Put(ctx, tok(3), map[string]any{"user_id": ana})
	must(err)
	for !seen[tok(3)] {
		next()
	}
	if p := sub.Position(); p > w.Offset {
		t.Fatalf("position %d past write offset %d before handling it", p, w.Offset)
	}

	// Delete, then recreate over the tombstone with If-Match.
	del, err := sessions.Delete(ctx, tok(3))
	must(err)
	_, err = sessions.Get(ctx, tok(3))
	if apiErr := requireCode(t, err, v1.ErrNotFound); apiErr.ETag != del.Version {
		t.Fatalf("tombstone ETag %q, want %q", apiErr.ETag, del.Version)
	}
	if _, err := sessions.Put(ctx, tok(3), map[string]any{"user_id": ana}, v1.IfMatch(del.Version)); err != nil {
		t.Fatalf("recreate over tombstone: %v", err)
	}

	// Revocation takes effect without a restart.
	_, err = dash.RevokeKey(ctx, key(), tenant.ID, dataKey.Key.ID)
	must(err)
	deadline := time.Now().Add(target.revocationWait)
	for {
		_, err = users.Get(ctx, ana)
		if errors.Is(err, v1.ErrInvalidKey) {
			break
		}
		if time.Now().After(deadline) {
			t.Fatalf("revoked key still accepted after %v: %v", target.revocationWait, err)
		}
		time.Sleep(100 * time.Millisecond)
	}
}
