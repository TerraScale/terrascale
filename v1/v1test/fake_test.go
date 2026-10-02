package v1test_test

// Fast-suite integration tests for the v1test contract fake. They speak raw
// HTTPS to the fake (not through the v1 client) so that the wire contract
// itself is what is checked.

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strconv"
	"strings"
	"testing"
	"time"

	"terrascale.tech/sdk/v1/v1test"
)

type reply struct {
	status int
	header http.Header
	body   map[string]any
}

func (r reply) code() string {
	e, _ := r.body["error"].(map[string]any)
	c, _ := e["code"].(string)
	return c
}

func (r reply) str(key string) string { s, _ := r.body[key].(string); return s }

type caller struct {
	t   *testing.T
	srv *v1test.Server
}

// do sends a request; body may be nil, a string or a value to marshal.
// headers are name/value pairs.
func (c caller) do(method, path, token string, body any, headers ...string) reply {
	c.t.Helper()
	var rd io.Reader
	switch b := body.(type) {
	case nil:
	case string:
		rd = strings.NewReader(b)
	default:
		data, err := json.Marshal(b)
		if err != nil {
			c.t.Fatal(err)
		}
		rd = bytes.NewReader(data)
	}
	req, err := http.NewRequestWithContext(c.t.Context(), method, c.srv.URL+path, rd)
	if err != nil {
		c.t.Fatal(err)
	}
	if token != "" {
		req.Header.Set("Authorization", "Bearer "+token)
	}
	if rd != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	for i := 0; i+1 < len(headers); i += 2 {
		req.Header.Set(headers[i], headers[i+1])
	}
	res, err := c.srv.Client().Do(req)
	if err != nil {
		c.t.Fatal(err)
	}
	defer res.Body.Close()
	data, err := io.ReadAll(res.Body)
	if err != nil {
		c.t.Fatal(err)
	}
	out := reply{status: res.StatusCode, header: res.Header}
	if len(data) > 0 {
		if err := json.Unmarshal(data, &out.body); err != nil {
			c.t.Fatalf("%s %s: non-JSON body %q", method, path, data)
		}
	}
	if res.StatusCode >= 400 {
		e, ok := out.body["error"].(map[string]any)
		if !ok || e["code"] == "" || res.Header.Get("Content-Type") != "application/json" {
			c.t.Fatalf("%s %s: %d without the error envelope: %q", method, path, res.StatusCode, data)
		}
	}
	return out
}

func (c caller) want(r reply, status int, code string) reply {
	c.t.Helper()
	if r.status != status || r.code() != code {
		c.t.Fatalf("want %d %q, got %d %v", status, code, r.status, r.body)
	}
	return r
}

func start(t *testing.T, opts v1test.Options) (*v1test.Server, caller) {
	t.Helper()
	srv, err := v1test.Start(opts)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(srv.Close)
	return srv, caller{t: t, srv: srv}
}

const (
	idA = "0192f0c4-7a1e-7c3d-9b2a-5e8f1d2c3b4a"
	idB = "0192f0c4-7a1e-7c3d-9b2a-5e8f1d2c3b4b"
	idC = "0192f0c4-7a1e-7c3d-9b2a-5e8f1d2c3b4c"
)

func TestControlPlaneProvisioningAndRevocation(t *testing.T) {
	t.Parallel()
	srv, c := start(t, v1test.Options{OperationDelay: time.Hour})
	ck := srv.ControlKey
	idem := func() []string {
		return []string{"Idempotency-Key", "k" + strconv.FormatInt(time.Now().UnixNano(), 36)}
	}

	c.want(c.do("GET", "/v1/readyz", "", nil), 200, "")
	c.want(c.do("POST", "/v1/dashboard/tenants", ck, map[string]any{"name": "Acme", "external_ref": "proj_1"}), 400, "idempotency_key_required")
	tenant := c.want(c.do("POST", "/v1/dashboard/tenants", ck, map[string]any{"name": "Acme", "external_ref": "proj_1"}, idem()...), 201, "")
	tid := tenant.str("id")
	c.want(c.do("POST", "/v1/dashboard/tenants", ck, map[string]any{"name": "Dup", "external_ref": "proj_1"}, idem()...), 409, "name_taken")
	list := c.want(c.do("GET", "/v1/dashboard/tenants?external_ref=proj_1", ck, nil), 200, "")
	if items := list.body["items"].([]any); len(items) != 1 || list.body["next_cursor"] != nil {
		t.Fatalf("tenant list: %v", list.body)
	}
	c.want(c.do("GET", "/v1/dashboard/tenants?bogus=1", ck, nil), 400, "invalid_request")

	base := "/v1/dashboard/tenants/" + tid
	c.want(c.do("POST", base+"/databases", ck, map[string]any{"name": "Bad Name"}, idem()...), 400, "invalid_request")
	c.want(c.do("POST", base+"/databases", ck, map[string]any{"name": "app"}, idem()...), 201, "")
	c.want(c.do("POST", base+"/databases/app/collections", ck, map[string]any{"name": "todos", "ttl_path": "/expires_at",
		"unique": []any{map[string]any{"name": "email", "paths": []string{"/email"}, "transform": "lower"}}}, idem()...), 201, "")
	coll := c.want(c.do("GET", base+"/databases/app/collections/todos", ck, nil), 200, "")
	if coll.body["ttl_path"] != "/expires_at" || coll.str("generation") != "1" {
		t.Fatalf("collection: %v", coll.body)
	}
	c.want(c.do("POST", base+"/keys", ck, map[string]any{"name": "s", "grants": []any{map[string]any{"database": "app",
		"collections": []string{"*"}, "permissions": []string{"read", "fly"}}}}, idem()...), 400, "invalid_request")
	created := c.want(c.do("POST", base+"/keys", ck, map[string]any{"name": "server", "grants": []any{map[string]any{"database": "app",
		"collections": []string{"*"}, "permissions": []string{"read", "write"}}}}, idem()...), 201, "")
	token := created.str("token")
	keyID := created.body["key"].(map[string]any)["id"].(string)
	if !strings.HasPrefix(token, "tbk1."+keyID+".") || len(token) != 5+22+1+43 {
		t.Fatalf("token shape: %q", token)
	}

	// Roles: control keys cannot touch data, data keys cannot touch the
	// control plane, and the audit log records the mutations.
	doc := "/v1/databases/app/collections/todos/documents/" + idA
	c.want(c.do("PUT", doc, ck, map[string]any{"email": "a@b.c"}), 403, "forbidden_scope")
	c.want(c.do("GET", base, token, nil), 403, "forbidden_scope")
	c.want(c.do("PUT", doc, token, map[string]any{"email": "a@b.c"}), 201, "")
	audit := c.want(c.do("GET", "/v1/dashboard/audit?tenant="+tid, ck, nil), 200, "")
	if n := len(audit.body["items"].([]any)); n < 5 {
		t.Fatalf("audit has %d events", n)
	}

	// Revocation takes effect on the next request.
	c.want(c.do("DELETE", base+"/keys/"+keyID, ck, nil, idem()...), 200, "")
	c.want(c.do("GET", doc, token, nil), 401, "invalid_key")

	// A database deletion is an operation; once it succeeds the database is
	// gone. Tenant deletion revokes every key of the tenant.
	other, err := srv.CreateDataKey(tid, v1test.Grant{Database: "*", Collections: []string{"*"}, Permissions: []string{"read"}})
	if err != nil {
		t.Fatal(err)
	}
	op := c.want(c.do("DELETE", base+"/databases/app", ck, nil, idem()...), 202, "")
	if op.str("status") != "running" || op.str("kind") != "delete_database" {
		t.Fatalf("operation: %v", op.body)
	}
	c.want(c.do("GET", "/v1/dashboard/operations/"+op.str("id"), ck, nil), 200, "")
	done := c.want(c.do("GET", "/v1/dashboard/operations/"+op.str("id"), ck, nil), 200, "")
	if done.str("status") != "succeeded" || done.str("finished_at") == "" {
		t.Fatalf("operation: %v", done.body)
	}
	c.want(c.do("GET", base+"/databases/app", ck, nil), 404, "not_found")
	c.want(c.do("GET", doc, other, nil), 404, "unknown_database")
	c.want(c.do("DELETE", base, ck, nil, idem()...), 202, "")
	c.want(c.do("GET", doc, other, nil), 403, "forbidden_scope") // tenant is deleting
	srv.Advance(2 * time.Hour)                                   // operations finish after OperationDelay
	c.want(c.do("GET", doc, other, nil), 401, "invalid_key")
	c.want(c.do("GET", base, ck, nil), 404, "not_found")
}

func TestGrantsAndKeyRoles(t *testing.T) {
	t.Parallel()
	srv, c := start(t, v1test.Options{Seed: true})
	tid := srv.Seed.TenantID
	readUsers, err := srv.CreateDataKey(tid, v1test.Grant{Database: "main", Collections: []string{"users"}, Permissions: []string{"read"}})
	if err != nil {
		t.Fatal(err)
	}
	users := "/v1/databases/main/collections/users/documents/" + idA
	c.want(c.do("GET", users, readUsers, nil), 404, "not_found")
	c.want(c.do("PUT", users, readUsers, map[string]any{"email": "x@y.z"}), 403, "forbidden_scope")
	c.want(c.do("GET", "/v1/databases/main/collections/orgs/documents/"+idA, readUsers, nil), 403, "forbidden_scope")
	// A named grant never reveals whether another database exists; a
	// wildcard grant learns unknown_database / unknown_collection.
	c.want(c.do("GET", "/v1/databases/nope/collections/users/documents/"+idA, readUsers, nil), 403, "forbidden_scope")
	c.want(c.do("GET", "/v1/databases/nope/collections/users/documents/"+idA, srv.Seed.DataKey, nil), 404, "unknown_database")
	c.want(c.do("GET", "/v1/databases/main/collections/nope/documents/"+idA, srv.Seed.DataKey, nil), 404, "unknown_collection")
	// Transactions check read on asserted and write on written collections.
	c.want(c.do("POST", "/v1/databases/main/transactions", readUsers, map[string]any{"ops": []any{
		map[string]any{"op": "assert", "collection": "users", "id": idA, "absent": true},
		map[string]any{"op": "create", "collection": "users", "document": map[string]any{}},
	}}), 403, "forbidden_scope")
	// Edge keys only read shapes; unknown and revoked keys are 401.
	edge := c.want(c.do("POST", "/v1/dashboard/edge-keys", srv.ControlKey, map[string]any{"name": "worker"}, "Idempotency-Key", "e1"), 201, "")
	c.want(c.do("GET", users, edge.str("token"), nil), 403, "forbidden_scope")
	c.want(c.do("GET", users, "tbk1.AAAAAAAAAAAAAAAAAAAAAA.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", nil), 401, "invalid_key")
	c.want(c.do("GET", users, "", nil), 401, "invalid_key")
	if !srv.RevokeKey(readUsers) {
		t.Fatal("RevokeKey: unknown key")
	}
	c.want(c.do("GET", users, readUsers, nil), 401, "invalid_key")
	// A suspended tenant's data keys are forbidden.
	c.want(c.do("PATCH", "/v1/dashboard/tenants/"+tid, srv.ControlKey, map[string]any{"status": "suspended"}, "Idempotency-Key", "s1"), 200, "")
	c.want(c.do("GET", users, srv.Seed.DataKey, nil), 403, "forbidden_scope")
}

func TestPreconditionsAndVersions(t *testing.T) {
	t.Parallel()
	srv, c := start(t, v1test.Options{Seed: true})
	key := srv.Seed.DataKey
	doc := "/v1/databases/main/collections/orgs/documents/" + idA

	created := c.want(c.do("PUT", doc, key, map[string]any{"name": "Acme"}, "If-None-Match", "*"), 201, "")
	v1 := created.str("version")
	if !strings.HasPrefix(v1, "tbv1-") || len(v1) != 37 || created.header.Get("ETag") != `"`+v1+`"` || created.str("offset") == "" {
		t.Fatalf("create: %v %v", created.body, created.header)
	}
	c.want(c.do("PUT", doc, key, map[string]any{"name": "Again"}, "If-None-Match", "*"), 412, "precondition_failed")
	got := c.want(c.do("GET", doc, key, nil), 200, "")
	if got.str("state") != "value" || got.str("version") != v1 {
		t.Fatalf("get: %v", got.body)
	}
	if r := c.do("GET", doc, key, nil, "If-None-Match", `"`+v1+`"`); r.status != 304 {
		t.Fatalf("conditional get: %d", r.status)
	}
	updated := c.want(c.do("PUT", doc, key, map[string]any{"name": "Acme 2"}, "If-Match", `"`+v1+`"`), 200, "")
	v2 := updated.str("version")
	c.want(c.do("PUT", doc, key, map[string]any{"name": "stale"}, "If-Match", `"`+v1+`"`), 412, "precondition_failed")
	c.want(c.do("PATCH", doc, key, `{"name":"stale"}`, "Content-Type", "application/merge-patch+json", "If-Match", `"`+v1+`"`), 412, "precondition_failed")
	c.want(c.do("DELETE", doc, key, nil, "If-Match", `"`+v1+`"`), 412, "precondition_failed")
	deleted := c.want(c.do("DELETE", doc, key, nil, "If-Match", `"`+v2+`"`), 200, "")
	tomb := deleted.str("version")
	gone := c.want(c.do("GET", doc, key, nil), 404, "not_found")
	if gone.header.Get("ETag") != `"`+tomb+`"` {
		t.Fatalf("deleted 404 ETag %q, want tombstone %q", gone.header.Get("ETag"), tomb)
	}
	c.want(c.do("DELETE", doc, key, nil), 404, "not_found")
	c.want(c.do("PUT", doc, key, map[string]any{"name": "Back"}, "If-Match", `"`+tomb+`"`), 201, "")
	c.want(c.do("PATCH", "/v1/databases/main/collections/orgs/documents/"+idB, key, `{"a":1}`, "Content-Type", "application/merge-patch+json"), 404, "not_found")
	c.want(c.do("PATCH", doc, key, `{"a":1}`, "Content-Type", "text/plain"), 415, "unsupported_media_type")
	c.want(c.do("GET", "/v1/databases/main/collections/orgs/documents/not-a-uuid", key, nil), 400, "invalid_request")
}

func TestUniqueConstraints(t *testing.T) {
	t.Parallel()
	srv, c := start(t, v1test.Options{})
	p, err := srv.Provision(v1test.ProvisionRequest{Database: "app", Collections: []v1test.CollectionSpec{
		{Name: "accounts", Unique: []v1test.Unique{{Name: "email", Paths: []string{"/email"}, Transform: "lower"}}},
	}})
	if err != nil {
		t.Fatal(err)
	}
	docs := "/v1/databases/app/collections/accounts/documents/"
	c.want(c.do("PUT", docs+idA, p.DataKey, map[string]any{"email": "Ana@Example.com"}), 201, "")
	dup := c.want(c.do("PUT", docs+idB, p.DataKey, map[string]any{"email": "ana@example.COM"}), 409, "unique_violation")
	if dup.body["error"].(map[string]any)["constraint"] != "email" {
		t.Fatalf("violation: %v", dup.body)
	}
	c.want(c.do("POST", "/v1/databases/app/collections/accounts/documents", p.DataKey, map[string]any{"email": "ANA@example.com"}), 409, "unique_violation")
	// Rewriting the owner, and documents missing or null at the path, are fine.
	c.want(c.do("PUT", docs+idA, p.DataKey, map[string]any{"email": "ana@example.com", "n": 2}), 200, "")
	c.want(c.do("PUT", docs+idB, p.DataKey, map[string]any{"email": nil}), 201, "")
	c.want(c.do("PUT", docs+idC, p.DataKey, map[string]any{"name": "no email"}), 201, "")
	// Inside a transaction, the violating op is named.
	tx := c.want(c.do("POST", "/v1/databases/app/transactions", p.DataKey, map[string]any{"ops": []any{
		map[string]any{"op": "patch", "collection": "accounts", "id": idC, "merge": map[string]any{"name": "x"}},
		map[string]any{"op": "create", "collection": "accounts", "document": map[string]any{"email": "Ana@example.com"}},
	}}), 409, "unique_violation")
	if e := tx.body["error"].(map[string]any); e["op"] != float64(1) || e["constraint"] != "email" {
		t.Fatalf("tx violation: %v", tx.body)
	}
	// Deleting the owner frees the value.
	c.want(c.do("DELETE", docs+idA, p.DataKey, nil), 200, "")
	c.want(c.do("PUT", docs+idC, p.DataKey, map[string]any{"email": "ana@EXAMPLE.com"}), 200, "")
	// Adding a constraint that existing documents violate fails.
	base := "/v1/dashboard/tenants/" + p.TenantID + "/databases/app/collections/accounts"
	c.want(c.do("PUT", docs+idA, p.DataKey, map[string]any{"handle": "dup"}), 201, "")
	c.want(c.do("PUT", docs+idB, p.DataKey, map[string]any{"handle": "dup"}), 200, "")
	c.want(c.do("PATCH", base, srv.ControlKey, map[string]any{"unique": []any{map[string]any{"name": "handle", "paths": []string{"/handle"}}}},
		"Idempotency-Key", "u1"), 409, "unique_violation")
}

func TestTTLHidesExpiredDocuments(t *testing.T) {
	t.Parallel()
	srv, c := start(t, v1test.Options{})
	p, err := srv.Provision(v1test.ProvisionRequest{Collections: []v1test.CollectionSpec{
		{Name: "sessions", TTLPath: "/expires_at", Unique: []v1test.Unique{{Name: "token", Paths: []string{"/token"}}}},
	}})
	if err != nil {
		t.Fatal(err)
	}
	key := p.DataKey
	docs := "/v1/databases/main/collections/sessions/documents/"
	past := time.Now().Add(-time.Minute).UTC().Format(time.RFC3339)
	soon := strconv.FormatInt(time.Now().Add(time.Hour).UnixMilli(), 10)

	c.want(c.do("PUT", docs+idA, key, `{"token":"t1","expires_at":"`+past+`"}`), 201, "")
	c.want(c.do("PUT", docs+idB, key, `{"token":"t2","expires_at":`+soon+`}`), 201, "")
	c.want(c.do("PUT", docs+idC, key, `{"token":"t3"}`), 201, "") // never expires
	c.want(c.do("PUT", docs+idC, key, `{"token":"t3","expires_at":"tomorrow"}`), 400, "ttl_invalid")
	c.want(c.do("PUT", docs+idC, key, `{"token":"t3","expires_at":true}`), 400, "ttl_invalid")

	c.want(c.do("GET", docs+idA, key, nil), 404, "not_found")
	c.want(c.do("GET", docs+idB, key, nil), 200, "")
	query := func() []string {
		r := c.want(c.do("POST", "/v1/databases/main/collections/sessions/query", key, map[string]any{"count": true}), 200, "")
		var ids []string
		for _, it := range r.body["items"].([]any) {
			ids = append(ids, it.(map[string]any)["id"].(string))
		}
		if r.body["count"].(map[string]any)["value"] != strconv.Itoa(len(ids)) {
			t.Fatalf("count mismatch: %v", r.body)
		}
		return ids
	}
	if ids := query(); strings.Join(ids, ",") != idB+","+idC {
		t.Fatalf("query sees %v", ids)
	}
	// An expired document counts as absent for asserts and If-None-Match,
	// and no longer holds its unique value.
	c.want(c.do("POST", "/v1/databases/main/transactions", key, map[string]any{"ops": []any{
		map[string]any{"op": "assert", "collection": "sessions", "id": idA, "absent": true},
	}}), 200, "")
	c.want(c.do("PUT", "/v1/databases/main/collections/sessions/documents/0192f0c4-7a1e-7c3d-9b2a-5e8f1d2c3b4d", key, `{"token":"t1"}`), 201, "")
	c.want(c.do("PUT", docs+idA, key, `{"token":"t9"}`, "If-None-Match", "*"), 201, "")

	// Advancing the clock past idB's instant expires it.
	srv.Advance(2 * time.Hour)
	c.want(c.do("GET", docs+idB, key, nil), 404, "not_found")
	c.want(c.do("PATCH", docs+idB, key, `{"a":1}`, "Content-Type", "application/merge-patch+json"), 404, "not_found")
}

func TestIdempotencyReplayConflictAndExpiry(t *testing.T) {
	t.Parallel()
	srv, c := start(t, v1test.Options{Seed: true})
	key := srv.Seed.DataKey
	post := "/v1/databases/main/collections/items/documents"
	first := c.want(c.do("POST", post, key, map[string]any{"n": 1}, "Idempotency-Key", "create-1"), 201, "")
	replay := c.want(c.do("POST", post, key, map[string]any{"n": 1}, "Idempotency-Key", "create-1"), 201, "")
	if replay.str("id") != first.str("id") || replay.str("offset") != first.str("offset") || replay.header.Get("Location") != first.header.Get("Location") {
		t.Fatalf("replay differs: %v vs %v", replay.body, first.body)
	}
	c.want(c.do("POST", post, key, map[string]any{"n": 2}, "Idempotency-Key", "create-1"), 409, "idempotency_conflict")
	c.want(c.do("POST", post, key, map[string]any{"n": 1}, "Idempotency-Key", "bad key!"), 400, "invalid_request")
	r := c.want(c.do("POST", "/v1/databases/main/collections/items/query", key, map[string]any{"count": true}), 200, "")
	if r.body["count"].(map[string]any)["value"] != "1" {
		t.Fatalf("replay wrote twice: %v", r.body)
	}
	// Idempotency keys are scoped per API key.
	other, err := srv.CreateDataKey(srv.Seed.TenantID, v1test.Grant{Database: "main", Collections: []string{"*"}, Permissions: []string{"write"}})
	if err != nil {
		t.Fatal(err)
	}
	if c.want(c.do("POST", post, other, map[string]any{"n": 1}, "Idempotency-Key", "create-1"), 201, "").str("id") == first.str("id") {
		t.Fatal("idempotency slot shared across keys")
	}
	srv.Advance(24 * time.Hour)
	c.want(c.do("POST", post, key, map[string]any{"n": 1}, "Idempotency-Key", "create-1"), 409, "idempotency_expired")
}

func TestTransactionsAndAsserts(t *testing.T) {
	t.Parallel()
	srv, c := start(t, v1test.Options{Seed: true, AssertCountLimit: 5})
	key := srv.Seed.DataKey
	tx := func(ops ...any) reply {
		return c.do("POST", "/v1/databases/main/transactions", key, map[string]any{"ops": ops})
	}
	ok := c.want(tx(
		map[string]any{"op": "create", "collection": "plans", "id": idA, "document": map[string]any{"visible": true}},
		map[string]any{"op": "put", "collection": "orgs", "id": idB, "document": map[string]any{"n": 1}, "if_none_match": "*"},
		map[string]any{"op": "assert_count", "collection": "plans", "filter": map[string]any{"eq": []any{"/visible", true}}, "lte": 0},
	), 200, "")
	results := ok.body["results"].([]any)
	if len(results) != 3 || results[2].(map[string]any)["count"] != "0" || ok.str("offset") == "" {
		t.Fatalf("tx: %v", ok.body)
	}
	orgVersion := results[1].(map[string]any)["version"].(string)
	// assert with filter, assert absent, failing assert_count.
	c.want(tx(
		map[string]any{"op": "assert", "collection": "plans", "id": idA, "filter": map[string]any{"eq": []any{"/visible", true}}},
		map[string]any{"op": "assert", "collection": "invites", "id": idC, "absent": true},
		map[string]any{"op": "patch", "collection": "orgs", "id": idB, "ops": []any{map[string]any{"op": "inc", "path": "/n", "by": 1}}, "if_match": orgVersion},
	), 200, "")
	failed := c.want(tx(
		map[string]any{"op": "patch", "collection": "orgs", "id": idB, "merge": map[string]any{"x": 1}},
		map[string]any{"op": "assert_count", "collection": "plans", "filter": map[string]any{"exists": "/visible"}, "gte": 2},
	), 412, "precondition_failed")
	if failed.body["error"].(map[string]any)["op"] != float64(1) {
		t.Fatalf("op index: %v", failed.body)
	}
	c.want(tx(
		map[string]any{"op": "patch", "collection": "orgs", "id": idB, "merge": map[string]any{"x": 1}, "if_match": orgVersion},
	), 412, "precondition_failed")
	c.want(tx(
		map[string]any{"op": "put", "collection": "orgs", "id": idC, "document": map[string]any{}},
		map[string]any{"op": "delete", "collection": "orgs", "id": idC},
	), 400, "invalid_request")
	c.want(tx(map[string]any{"op": "create", "collection": "orgs", "document": map[string]any{}, "if_none_match": "*"}), 400, "invalid_request")
	c.want(tx(map[string]any{"op": "patch", "collection": "orgs", "id": idC, "merge": map[string]any{}}), 404, "not_found")
	// The rolled-back transactions left the org at n=2 without x.
	got := c.want(c.do("GET", "/v1/databases/main/collections/orgs/documents/"+idB, key, nil), 200, "")
	if d := got.body["document"].(map[string]any); d["n"] != float64(2) || d["x"] != nil {
		t.Fatalf("org: %v", d)
	}
	for i := range 6 {
		c.want(c.do("PUT", fmt.Sprintf("/v1/databases/main/collections/sessions/documents/0192f0c4-7a1e-7c3d-9b2a-%012d", i), key, map[string]any{}), 201, "")
	}
	c.want(tx(map[string]any{"op": "assert_count", "collection": "sessions", "filter": map[string]any{"exists": "/u"}, "lte": 1}), 400, "assert_requires_index")
}

func TestQueryFilterSortCursorCount(t *testing.T) {
	t.Parallel()
	srv, c := start(t, v1test.Options{Seed: true})
	key := srv.Seed.DataKey
	for i := range 7 {
		c.want(c.do("PUT", fmt.Sprintf("/v1/databases/main/collections/items/documents/0192f0c4-7a1e-7c3d-9b2a-%012d", i), key,
			fmt.Sprintf(`{"rank":%d,"team":"%s","name":"N%d"}`, i%4, []string{"Red", "blue"}[i%2], i)), 201, "")
	}
	q := map[string]any{"filter": map[string]any{"eq": []any{map[string]any{"lower": "/team"}, "red"}},
		"sort": []any{map[string]any{"path": "/rank", "order": "desc"}}, "project": []string{"/rank"}, "limit": 2, "count": true}
	var ranks []float64
	pages := 0
	for {
		r := c.want(c.do("POST", "/v1/databases/main/collections/items/query", key, q), 200, "")
		pages++
		if pages == 1 && r.body["count"].(map[string]any)["value"] != "4" {
			t.Fatalf("count: %v", r.body)
		}
		if pages > 1 && r.body["count"] != nil {
			t.Fatalf("count on a cursor page: %v", r.body)
		}
		for _, it := range r.body["items"].([]any) {
			d := it.(map[string]any)["document"].(map[string]any)
			if _, leaked := d["name"]; leaked {
				t.Fatalf("projection leaked: %v", d)
			}
			ranks = append(ranks, d["rank"].(float64))
		}
		next, _ := r.body["next_cursor"].(string)
		if next == "" {
			break
		}
		q["cursor"] = next
	}
	if fmt.Sprint(ranks) != "[2 2 0 0]" || pages != 2 {
		t.Fatalf("ranks %v over %d pages", ranks, pages)
	}
	// A cursor from another query is rejected.
	q["filter"] = map[string]any{"exists": "/team"}
	c.want(c.do("POST", "/v1/databases/main/collections/items/query", key, q), 400, "invalid_cursor")
	c.want(c.do("POST", "/v1/databases/main/collections/items/query", key, map[string]any{"filter": map[string]any{"bogus": 1}}), 400, "invalid_query")
	c.want(c.do("POST", "/v1/databases/main/collections/items/query", key, map[string]any{"unknown": 1}), 400, "invalid_query")
}

func TestErrorEnvelopeAndRoutes(t *testing.T) {
	t.Parallel()
	srv, c := start(t, v1test.Options{Seed: true})
	c.want(c.do("GET", "/v1/nope", srv.Seed.DataKey, nil), 404, "unknown_route")
	c.want(c.do("POST", "/v1/databases/main/collections/items/documents/"+idA, srv.Seed.DataKey, map[string]any{}), 405, "method_not_allowed")
	c.want(c.do("DELETE", "/v1/dashboard/regions", srv.ControlKey, nil), 405, "method_not_allowed")
	c.want(c.do("PUT", "/v1/databases/main/collections/items/documents/"+idA, srv.Seed.DataKey, `[1]`), 400, "invalid_request")
	c.want(c.do("PUT", "/v1/databases/main/collections/items/documents/"+idA, srv.Seed.DataKey, `{}`, "Content-Encoding", "gzip"), 415, "unsupported_media_type")
	c.want(c.do("PUT", "/v1/databases/main/collections/items/documents/"+idA, srv.Seed.DataKey, `{"a":"`+strings.Repeat("x", 4<<20)+`"}`), 413, "payload_too_large")
	jwks := c.want(c.do("GET", "/v1/.well-known/terrabase-edge-jwks.json", "", nil), 200, "")
	if k := jwks.body["keys"].([]any)[0].(map[string]any); k["kty"] != "OKP" || k["crv"] != "Ed25519" {
		t.Fatalf("jwks: %v", jwks.body)
	}
	regions := c.want(c.do("GET", "/v1/dashboard/regions", srv.ControlKey, nil), 200, "")
	if _, ok := regions.body["next_cursor"]; !ok {
		t.Fatalf("regions list shape: %v", regions.body)
	}
	srv.InjectFault(v1test.Fault{Method: "GET", PathPrefix: "/v1/databases/", Status: 429, Code: "rate_limited", RetryAfter: "1"})
	r := c.want(c.do("GET", "/v1/databases/main/collections/items/documents/"+idA, srv.Seed.DataKey, nil), 429, "rate_limited")
	if r.header.Get("Retry-After") != "1" || srv.FaultsLeft() != 0 {
		t.Fatalf("fault: %v", r.header)
	}
}
