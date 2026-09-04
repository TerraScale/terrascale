// Conformance stub for GOAL P-2b (Go SDK surface).
//
// Spins an httptest stub of /api/v1 and exercises the full core surface:
// document CRUD + history, schema builder + apply, typed SQL builder + raw
// parameterized SQL, subscribe/execute with checkpoints and typed resets,
// region pinning with 421 repair, tenants, and management parity.
package sdk_test

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"

	sdk "terrascale.tech/sdk"
)

type stubState struct {
	mu        sync.Mutex
	docs      map[string]json.RawMessage
	logCalls  map[string]int
	pinHits   []string
	pinFailed bool
}

func newStub(t *testing.T) (*stubState, *httptest.Server) {
	t.Helper()
	st := &stubState{docs: map[string]json.RawMessage{}, logCalls: map[string]int{}}
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		write := func(v any) {
			w.Header().Set("Content-Type", "application/json")
			_ = json.NewEncoder(w).Encode(v)
		}
		p := r.URL.Path
		switch {
		// --- documents ---
		case p == "/api/v1/databases/db1/collections/users/documents" && r.Method == http.MethodPost:
			var req struct {
				Body json.RawMessage `json:"body"`
			}
			_ = json.NewDecoder(r.Body).Decode(&req)
			st.mu.Lock()
			id := "doc-1"
			st.docs[id] = req.Body
			st.mu.Unlock()
			write(map[string]any{"id": id, "collection": "users", "body": req.Body, "revision": "rev-1"})
		case p == "/api/v1/databases/db1/collections/users/documents" && r.Method == http.MethodGet:
			st.mu.Lock()
			docs := []map[string]any{}
			for id, body := range st.docs {
				docs = append(docs, map[string]any{"id": id, "collection": "users", "body": body, "revision": "rev-1"})
			}
			st.mu.Unlock()
			write(map[string]any{"documents": docs})
		case strings.HasPrefix(p, "/api/v1/databases/db1/collections/users/documents/"):
			rest := strings.TrimPrefix(p, "/api/v1/databases/db1/collections/users/documents/")
			if strings.HasSuffix(rest, "/history") && r.Method == http.MethodGet {
				write(map[string]any{"history": []map[string]any{
					{"revision": "rev-1", "body": json.RawMessage(`{"name":"a"}`), "dot": "(sfo,7)", "reason": "merge-winner"},
				}})
				return
			}
			id := rest
			switch r.Method {
			case http.MethodGet:
				st.mu.Lock()
				body, ok := st.docs[id]
				st.mu.Unlock()
				if !ok {
					w.WriteHeader(http.StatusNotFound)
					write(map[string]any{"code": "not_found", "message": "no such document"})
					return
				}
				write(map[string]any{"id": id, "collection": "users", "body": body, "revision": "rev-1"})
			case http.MethodPut, http.MethodPatch:
				var req struct {
					Body json.RawMessage `json:"body"`
				}
				_ = json.NewDecoder(r.Body).Decode(&req)
				st.mu.Lock()
				st.docs[id] = req.Body
				st.mu.Unlock()
				write(map[string]any{"id": id, "collection": "users", "body": req.Body, "revision": "rev-2"})
			case http.MethodDelete:
				st.mu.Lock()
				delete(st.docs, id)
				st.mu.Unlock()
				w.WriteHeader(http.StatusNoContent)
			default:
				w.WriteHeader(http.StatusMethodNotAllowed)
			}
		// --- sql ---
		case p == "/api/v1/databases/db1/sql" && r.Method == http.MethodPost:
			var req struct {
				Statement string `json:"statement"`
				Params    []any  `json:"params"`
			}
			_ = json.NewDecoder(r.Body).Decode(&req)
			if !strings.HasPrefix(strings.ToLower(strings.TrimSpace(req.Statement)), "select") {
				w.WriteHeader(http.StatusBadRequest)
				write(map[string]any{"code": "not_subscribable", "message": "not a read-only statement"})
				return
			}
			write(map[string]any{
				"columns": []string{"id", "name"},
				"rows":    []map[string]any{{"id": "doc-1", "name": "a"}},
			})
		// --- schema ---
		case p == "/api/v1/databases/db1/schema" && r.Method == http.MethodPut:
			var schema sdk.Schema
			_ = json.NewDecoder(r.Body).Decode(&schema)
			write(schema)
		// --- reactive attach ---
		case p == "/api/v1/reactive/views" && r.Method == http.MethodPost:
			var req struct {
				Statement string `json:"statement"`
			}
			_ = json.NewDecoder(r.Body).Decode(&req)
			scheme := "http://" + r.Host
			if strings.Contains(req.Statement, "RESET_ME") {
				write(map[string]any{
					"viewId": "view-reset", "generation": 3, "checkpoint": "cp0",
					"snapshot": []any{}, "logUrl": scheme + "/log-reset", "capability": "cap-reset",
				})
				return
			}
			write(map[string]any{
				"viewId": "view-1", "generation": 2, "checkpoint": "cp0",
				"snapshot": []any{
					map[string]any{"id": "doc-1", "collection": "users", "body": json.RawMessage(`{"name":"a"}`), "revision": "rev-1"},
				},
				"logUrl": scheme + "/log", "capability": "cap-1",
			})
		// --- reactive log ---
		case p == "/log" && r.Method == http.MethodGet:
			if r.Header.Get("X-TerraScale-View-Capability") != "cap-1" {
				w.WriteHeader(http.StatusUnauthorized)
				write(map[string]any{"code": "forbidden", "message": "bad capability"})
				return
			}
			st.mu.Lock()
			n := st.logCalls["log"]
			st.logCalls["log"] = n + 1
			st.mu.Unlock()
			if r.URL.Query().Get("offset") == "cp0" && n == 0 {
				write(map[string]any{
					"updates": []any{
						map[string]any{"kind": "insert", "checkpoint": "cp1", "id": "doc-2", "collection": "users", "body": json.RawMessage(`{"name":"b"}`)},
					},
					"checkpoint": "cp1",
				})
				return
			}
			write(map[string]any{"updates": []any{}, "checkpoint": "cp1"})
		case p == "/log-reset" && r.Method == http.MethodGet:
			write(map[string]any{
				"reset":      map[string]any{"reason": "schema_changed", "message": "schema version bumped"},
				"checkpoint": "cp9",
			})
		// --- tenants ---
		case p == "/api/v1/tenants" && r.Method == http.MethodPost:
			var req sdk.CreateTenantRequest
			_ = json.NewDecoder(r.Body).Decode(&req)
			write(sdk.Tenant{ID: "ten-1", Key: req.Key, DatabaseID: "db-ten-1", Regions: req.Regions})
		case p == "/api/v1/tenants" && r.Method == http.MethodGet:
			write(map[string]any{"tenants": []sdk.Tenant{{ID: "ten-1", Key: "acme", DatabaseID: "db-ten-1"}}})
		case strings.HasPrefix(p, "/api/v1/tenants/") && strings.HasSuffix(p, "/keys") && r.Method == http.MethodPost:
			write(sdk.TenantKey{KeyID: "key-1", Token: "tenant-token"})
		case strings.HasPrefix(p, "/api/v1/tenants/") && r.Method == http.MethodGet:
			key := strings.TrimPrefix(p, "/api/v1/tenants/")
			write(sdk.Tenant{ID: "ten-1", Key: key, DatabaseID: "db-ten-1"})
		case strings.HasPrefix(p, "/api/v1/tenants/") && r.Method == http.MethodDelete:
			w.WriteHeader(http.StatusNoContent)
		// --- management ---
		case p == "/api/v1/organizations" && r.Method == http.MethodGet:
			write([]sdk.Organization{{ID: "org-1", Name: "Acme"}})
		case p == "/api/v1/projects/p1/databases" && r.Method == http.MethodPost:
			write(sdk.ManagedDatabase{ID: "db-1", ProjectID: "p1", Name: "primary"})
		case p == "/api/v1/regions" && r.Method == http.MethodGet:
			write(map[string]any{"regions": []sdk.Region{{Code: sdk.RegionGRU, DisplayName: "Sao Paulo", Available: true}}})
		default:
			w.WriteHeader(http.StatusNotFound)
			write(map[string]any{"code": "not_found", "message": "stub: no route for " + r.Method + " " + p})
		}
	}))
	t.Cleanup(srv.Close)
	return st, srv
}

func newClient(t *testing.T, srv *httptest.Server) *sdk.Client {
	t.Helper()
	c, err := sdk.NewClient(sdk.Config{APIBaseURL: srv.URL, AccessToken: "test-token", ProjectID: "p1"})
	if err != nil {
		t.Fatalf("NewClient: %v", err)
	}
	return c
}

func recv(t *testing.T, ch <-chan sdk.Update) sdk.Update {
	t.Helper()
	select {
	case u, ok := <-ch:
		if !ok {
			t.Fatalf("event channel closed unexpectedly")
		}
		return u
	case <-time.After(5 * time.Second):
		t.Fatalf("timed out waiting for subscription event")
		return sdk.Update{}
	}
}

func TestP2BConformanceDocumentCRUD(t *testing.T) {
	_, srv := newStub(t)
	c := newClient(t, srv)
	ctx := context.Background()

	created, err := c.Documents.Create(ctx, "db1", "users", map[string]any{"name": "a"})
	if err != nil {
		t.Fatalf("Create: %v", err)
	}
	if created.ID == "" || created.Revision == "" {
		t.Fatalf("Create returned thin document: %+v", created)
	}

	got, err := c.Documents.Get(ctx, "db1", "users", created.ID)
	if err != nil {
		t.Fatalf("Get: %v", err)
	}
	if string(got.Body) != `{"name":"a"}` {
		t.Fatalf("Get body = %s", got.Body)
	}

	list, err := c.Documents.List(ctx, "db1", "users", 25, "")
	if err != nil {
		t.Fatalf("List: %v", err)
	}
	if len(list.Documents) != 1 {
		t.Fatalf("List count = %d", len(list.Documents))
	}

	updated, err := c.Documents.Update(ctx, "db1", "users", created.ID, map[string]any{"name": "b"})
	if err != nil {
		t.Fatalf("Update: %v", err)
	}
	if string(updated.Body) != `{"name":"b"}` {
		t.Fatalf("Update body = %s", updated.Body)
	}

	patched, err := c.Documents.Patch(ctx, "db1", "users", created.ID, map[string]any{"name": "c"})
	if err != nil {
		t.Fatalf("Patch: %v", err)
	}
	if string(patched.Body) != `{"name":"c"}` {
		t.Fatalf("Patch body = %s", patched.Body)
	}

	history, err := c.Documents.History(ctx, "db1", "users", created.ID)
	if err != nil {
		t.Fatalf("History: %v", err)
	}
	if len(history) != 1 || history[0].Revision == "" {
		t.Fatalf("History = %+v", history)
	}

	if err := c.Documents.Delete(ctx, "db1", "users", created.ID); err != nil {
		t.Fatalf("Delete: %v", err)
	}
}

func TestP2BConformanceSchemaBuilder(t *testing.T) {
	_, srv := newStub(t)
	c := newClient(t, srv)
	ctx := context.Background()

	users, err := sdk.NewCollection("users").
		Field("name", sdk.FieldString, sdk.Required(), sdk.Indexed()).
		Field("age", sdk.FieldInt, sdk.Indexed()).
		Field("tags", sdk.FieldSetAddWins).
		HistoryRetention(7).
		Build()
	if err != nil {
		t.Fatalf("Build collection: %v", err)
	}
	b := sdk.NewSchemaBuilder().AddCollection(users)
	applied, err := b.Apply(ctx, c, "db1")
	if err != nil {
		t.Fatalf("Apply: %v", err)
	}
	if len(applied.Collections) != 1 || len(applied.Collections[0].Fields) != 3 {
		t.Fatalf("Applied schema = %+v", applied)
	}

	if _, err := sdk.NewCollection("users").Field("a", sdk.FieldString).Field("a", sdk.FieldInt).Build(); err == nil {
		t.Fatalf("expected duplicate field error")
	}
	if _, err := sdk.NewCollection("users").HistoryRetention(90).Build(); err == nil {
		t.Fatalf("expected retention bound error")
	}
}

func TestP2BConformanceSQLBuilder(t *testing.T) {
	// Typed builder parameterizes values; the statement must not contain them.
	qb := sdk.Select("users", "id", "name").
		Where(sdk.And(sdk.Eq("name", "o'hara"), sdk.Gte("age", 21))).
		OrderBy("age DESC").
		Limit(10)
	statement, params, err := qb.Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if strings.Contains(statement, "o'hara") {
		t.Fatalf("statement interpolates values: %s", statement)
	}
	if len(params) != 3 {
		t.Fatalf("params = %v", params)
	}

	_, srv := newStub(t)
	c := newClient(t, srv)
	ctx := context.Background()

	res, err := c.ExecuteQuery(ctx, "db1", qb)
	if err != nil {
		t.Fatalf("ExecuteQuery: %v", err)
	}
	if res.Len() != 1 || len(res.Columns) != 2 {
		t.Fatalf("result = %+v", res)
	}

	raw, err := c.ExecuteSQL(ctx, "db1", "SELECT * FROM users WHERE active = ?", []any{true})
	if err != nil {
		t.Fatalf("ExecuteSQL: %v", err)
	}
	if raw.Len() != 1 {
		t.Fatalf("raw result = %+v", raw)
	}

	if _, err := c.ExecuteSQL(ctx, "db1", "DROP TABLE users", nil); err == nil {
		t.Fatalf("expected read-only rejection for DDL")
	}
}

func TestP2BConformanceSubscribeExecute(t *testing.T) {
	_, srv := newStub(t)
	c := newClient(t, srv)
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()

	snap, err := c.Execute(ctx, "db1", "SELECT * FROM users", nil)
	if err != nil {
		t.Fatalf("Execute: %v", err)
	}
	if snap.ViewID == "" || snap.Checkpoint != "cp0" || len(snap.Documents) != 1 {
		t.Fatalf("snapshot = %+v", snap)
	}

	sub, err := c.Subscribe(ctx, "db1", "SELECT * FROM users", nil, sdk.SubscriptionOptions{})
	if err != nil {
		t.Fatalf("Subscribe: %v", err)
	}
	defer sub.Close()

	first := recv(t, sub.Events())
	if first.Kind != sdk.UpdateSnapshot || first.ID != "doc-1" {
		t.Fatalf("first event = %+v", first)
	}
	second := recv(t, sub.Events())
	if second.Kind != sdk.UpdateInsert || second.ID != "doc-2" {
		t.Fatalf("second event = %+v", second)
	}
	if sub.Checkpoint() != "cp1" {
		t.Fatalf("checkpoint = %q", sub.Checkpoint())
	}
	sub.Close()
}

func TestP2BConformanceTypedReset(t *testing.T) {
	_, srv := newStub(t)
	c := newClient(t, srv)
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()

	sub, err := c.Subscribe(ctx, "db1", "SELECT * FROM users RESET_ME", nil, sdk.SubscriptionOptions{})
	if err != nil {
		t.Fatalf("Subscribe: %v", err)
	}
	defer sub.Close()

	ev := recv(t, sub.Events())
	if ev.Kind != sdk.UpdateReset || ev.Reset == nil {
		t.Fatalf("expected typed reset, got %+v", ev)
	}
	if ev.Reset.Reason != sdk.ResetSchemaChanged {
		t.Fatalf("reset reason = %q", ev.Reset.Reason)
	}
}

func TestP2BConformanceRegionPinning(t *testing.T) {
	var mu sync.Mutex
	hints := []string{}
	calls := 0
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		mu.Lock()
		calls++
		n := calls
		mu.Unlock()
		hint := r.Header.Get("X-TerraScale-Preferred-Region")
		mu.Lock()
		hints = append(hints, hint)
		mu.Unlock()
		if n == 1 {
			w.Header().Set("X-TerraScale-Placement", "SFO")
			w.WriteHeader(421)
			_, _ = w.Write([]byte(`{"code":"misdirected","message":"not hosted here"}`))
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"regions":[{"code":"SFO","available":true}]}`))
	}))
	defer srv.Close()

	c, err := sdk.NewClient(sdk.Config{APIBaseURL: srv.URL, PreferredRegion: sdk.RegionGRU})
	if err != nil {
		t.Fatalf("NewClient: %v", err)
	}
	regions, err := c.Regions.List(context.Background())
	if err != nil {
		t.Fatalf("List after 421: %v", err)
	}
	if len(regions) != 1 {
		t.Fatalf("regions = %+v", regions)
	}
	if got := c.PinnedRegion(); got != sdk.RegionSFO {
		t.Fatalf("pinned = %q", got)
	}
	mu.Lock()
	defer mu.Unlock()
	if len(hints) < 2 || hints[0] != "GRU" || hints[1] != "SFO" {
		t.Fatalf("region hints = %v", hints)
	}
}

func TestP2BConformanceTenantsManagement(t *testing.T) {
	_, srv := newStub(t)
	c := newClient(t, srv)
	ctx := context.Background()

	tenant, err := c.Tenants.Create(ctx, sdk.CreateTenantRequest{Key: "acme", Regions: []sdk.RegionCode{sdk.RegionGRU}})
	if err != nil {
		t.Fatalf("Tenants.Create: %v", err)
	}
	if tenant.DatabaseID == "" {
		t.Fatalf("tenant = %+v", tenant)
	}
	if _, err := c.Tenants.Get(ctx, "acme"); err != nil {
		t.Fatalf("Tenants.Get: %v", err)
	}
	list, err := c.Tenants.List(ctx, 25, "")
	if err != nil || len(list) != 1 {
		t.Fatalf("Tenants.List = %+v, err = %v", list, err)
	}
	key, err := c.Tenants.MintKey(ctx, "acme")
	if err != nil || key.Token == "" {
		t.Fatalf("Tenants.MintKey = %+v, err = %v", key, err)
	}

	orgs, err := c.Organizations.List(ctx, 0, 25)
	if err != nil || len(orgs) != 1 {
		t.Fatalf("Organizations.List = %+v, err = %v", orgs, err)
	}
	db, err := c.Databases.Create(ctx, "p1", "primary")
	if err != nil || db.ID == "" {
		t.Fatalf("Databases.Create = %+v, err = %v", db, err)
	}
	regions, err := c.Regions.List(ctx)
	if err != nil || len(regions) != 1 {
		t.Fatalf("Regions.List = %+v, err = %v", regions, err)
	}
}

func TestP2BConformanceClientValidation(t *testing.T) {
	if _, err := sdk.NewClient(sdk.Config{APIBaseURL: "https://api.terrascale.tech", Affinity: sdk.AffinityStrict}); err == nil {
		t.Fatalf("expected strict-without-region error")
	}
	if _, err := sdk.NewClient(sdk.Config{APIBaseURL: "https://tbep.terrascale.tech"}); err == nil {
		t.Fatalf("expected TBEP rejection")
	}
	if _, err := sdk.ParseRegionCode("gru"); err != nil {
		t.Fatalf("ParseRegionCode should be case-insensitive: %v", err)
	}
	if _, _, err := sdk.Select("", "id").Build(); err == nil {
		t.Fatalf("expected empty-collection build error")
	}
	if _, _, err := sdk.Select("users", "id").Limit(-1).Build(); err == nil {
		t.Fatalf("expected negative-limit build error")
	}
}
