package v1_test

// Fast-suite parity test: every request the v1 client can make must be a
// route of the published contract, and every contract route must be reachable
// from the client. The client is driven over real HTTPS against a recording
// server, so the check covers the paths the client actually builds.
//
// Source of truth: testdata/client-openapi.json, a pinned copy of terrabase
// crates/tb-server/assets/client-openapi.json, once it carries the v1 routes
// (TB-0, branch feat/v1-openapi-seams). Until then the file is absent (the
// seams branch still serves the legacy UUID/shard routes) and the route table
// testdata/v1-routes.txt, pinned from the contract docs at 7bc74f9d, is used.
// To pin a new OpenAPI copy:
//
//	git -C ../terrabase show <commit>:crates/tb-server/assets/client-openapi.json > v1/testdata/client-openapi.json

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"io/fs"
	"maps"
	"net/http"
	"net/http/httptest"
	"os"
	"slices"
	"strings"
	"sync"
	"testing"
	"time"

	v1 "terrascale.tech/sdk/v1"
)

// v1RoutePrefixes are the route families the v1 client implements. Service
// routes (/v1/docs, /v1/openapi.json, JWKS, admin) are out of scope.
var v1RoutePrefixes = []string{"/v1/databases/", "/v1/shapes/", "/v1/dashboard/"}

func inV1Scope(path string) bool {
	return slices.ContainsFunc(v1RoutePrefixes, func(p string) bool { return strings.HasPrefix(path, p) })
}

// contractRoutes returns "METHOD /template" routes and whether they came from
// the pinned OpenAPI file, plus the error code enum when the OpenAPI has one.
func contractRoutes(t *testing.T) (routes []string, fromOpenAPI bool, codes []string) {
	t.Helper()
	data, err := os.ReadFile("testdata/client-openapi.json")
	switch {
	case errors.Is(err, fs.ErrNotExist):
	case err != nil:
		t.Fatal(err)
	default:
		var doc struct {
			Paths map[string]map[string]json.RawMessage `json:"paths"`
		}
		if err := json.Unmarshal(data, &doc); err != nil {
			t.Fatalf("testdata/client-openapi.json: %v", err)
		}
		for path, ops := range doc.Paths {
			if !inV1Scope(path) {
				continue
			}
			for method := range ops {
				switch m := strings.ToUpper(method); m {
				case "GET", "PUT", "POST", "PATCH", "DELETE":
					routes = append(routes, m+" "+path)
				}
			}
		}
		if len(routes) == 0 {
			t.Fatal("testdata/client-openapi.json has no v1 data, shapes or dashboard routes; pin the v1 OpenAPI or delete the file")
		}
		var root any
		if err := json.Unmarshal(data, &root); err != nil {
			t.Fatal(err)
		}
		return routes, true, errorCodeEnum(root)
	}
	f, err := os.Open("testdata/v1-routes.txt")
	if err != nil {
		t.Fatal(err)
	}
	defer f.Close()
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		if line := strings.TrimSpace(sc.Text()); line != "" && !strings.HasPrefix(line, "#") {
			routes = append(routes, line)
		}
	}
	if err := sc.Err(); err != nil {
		t.Fatal(err)
	}
	return routes, false, nil
}

// errorCodeEnum collects the enum of every schema property named "code".
func errorCodeEnum(v any) []string {
	var out []string
	var walk func(any)
	walk = func(v any) {
		switch v := v.(type) {
		case map[string]any:
			if props, ok := v["properties"].(map[string]any); ok {
				if code, ok := props["code"].(map[string]any); ok {
					for _, e := range asSlice(code["enum"]) {
						if s, ok := e.(string); ok {
							out = append(out, s)
						}
					}
				}
			}
			for _, child := range v {
				walk(child)
			}
		case []any:
			for _, child := range v {
				walk(child)
			}
		}
	}
	walk(v)
	slices.Sort(out)
	return slices.Compact(out)
}

func asSlice(v any) []any { s, _ := v.([]any); return s }

// matchRoute maps a concrete request to the template it instantiates.
func matchRoute(routes []string, method, path string) (string, bool) {
	segs := strings.Split(strings.Trim(path, "/"), "/")
	for _, r := range routes {
		m, tpl, _ := strings.Cut(r, " ")
		if m != method {
			continue
		}
		tsegs := strings.Split(strings.Trim(tpl, "/"), "/")
		if len(tsegs) != len(segs) {
			continue
		}
		ok := true
		for i, s := range tsegs {
			if !(strings.HasPrefix(s, "{") && strings.HasSuffix(s, "}") && segs[i] != "") && s != segs[i] {
				ok = false
				break
			}
		}
		if ok {
			return r, true
		}
	}
	return "", false
}

// clientErrorCodes lists every ErrorCode the client declares for server
// responses (not_modified is synthesized from a bodiless 304).
var clientErrorCodes = []v1.ErrorCode{
	v1.ErrInvalidRequest, v1.ErrInvalidQuery, v1.ErrInvalidCursor, v1.ErrInvalidPatch, v1.ErrTTLInvalid,
	v1.ErrSortRequiresIndex, v1.ErrAssertRequiresIndex, v1.ErrInvalidKey, v1.ErrInvalidToken, v1.ErrForbiddenScope,
	v1.ErrNotFound, v1.ErrUnknownDatabase, v1.ErrUnknownCollection, v1.ErrUnknownRoute, v1.ErrMethodNotAllowed,
	v1.ErrConflictUnresolved, v1.ErrUniqueViolation, v1.ErrPatchConflict, v1.ErrIndexLimitExceeded,
	v1.ErrIdempotencyConflict, v1.ErrIdempotencyExpired, v1.ErrSchemaChanged, v1.ErrMustRefetch,
	v1.ErrPreconditionFailed, v1.ErrPayloadTooLarge, v1.ErrTransactionTooLarge, v1.ErrUnsupportedMediaType,
	v1.ErrHomeRegionRequired, v1.ErrRateLimited, v1.ErrInternalError, v1.ErrUpstreamUnavailable, v1.ErrCapacity,
	v1.ErrStorageUnavailable, v1.ErrStoredCorruption, v1.ErrCommitUncertain, v1.ErrDependenciesUnavail,
	v1.ErrNameTaken, v1.ErrIndexProtected, v1.ErrInvalidSchema, v1.ErrCodecConversion, v1.ErrLimitExceeded,
	v1.ErrIdempotencyKeyRequired,
}

func TestOpenAPIParity(t *testing.T) {
	t.Parallel()
	routes, fromOpenAPI, codes := contractRoutes(t)
	if !fromOpenAPI {
		t.Log("testdata/client-openapi.json is not pinned yet (TB-0); checking against testdata/v1-routes.txt")
	}

	var (
		mu   sync.Mutex
		seen = map[string]bool{}
		bad  []string
	)
	srv := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		mu.Lock()
		if tpl, ok := matchRoute(routes, r.Method, r.URL.Path); ok {
			seen[tpl] = true
		} else {
			bad = append(bad, r.Method+" "+r.URL.Path)
		}
		mu.Unlock()
		w.Header().Set("Content-Type", "application/json")
		switch {
		case r.Method == http.MethodPost && strings.HasSuffix(r.URL.Path, "/shapes"):
			json.NewEncoder(w).Encode(map[string]any{"shape_id": strings.Repeat("ab", 16), "handle": "h1", "offset": "-1",
				"url": "https://edge.invalid/v1/shapes/" + strings.Repeat("ab", 16), "token": "t", "expires_at": "4102444800000"})
		case strings.HasPrefix(r.URL.Path, "/v1/shapes/"):
			w.WriteHeader(http.StatusBadRequest)
			w.Write([]byte(`{"error":{"code":"invalid_request"}}`))
		case strings.HasPrefix(r.URL.Path, "/v1/dashboard/operations/"):
			w.Write([]byte(`{"id":"op","kind":"delete_tenant","status":"succeeded"}`))
		default:
			w.Write([]byte(`{}`))
		}
	}))
	srv.EnableHTTP2 = true
	srv.StartTLS()
	t.Cleanup(srv.Close)

	ctx, cancel := context.WithTimeout(t.Context(), 10*time.Second)
	defer cancel()
	token := "tbk1.AAAAAAAAAAAAAAAAAAAAAA.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
	cfg := v1.Config{BaseURL: srv.URL, Token: token, HTTPClient: srv.Client(), MaxRetries: -1}
	c, err := v1.New(cfg)
	if err != nil {
		t.Fatal(err)
	}
	d, err := v1.NewDashboard(cfg)
	if err != nil {
		t.Fatal(err)
	}

	// Drive every client operation. Responses are placeholders, so decode
	// errors are expected and ignored; only the requests matter.
	db := c.Database("main")
	col := db.Collection("items")
	id := v1.DocumentIDFor("items", "x")
	col.Get(ctx, id)
	col.Put(ctx, id, map[string]any{"a": 1})
	col.Create(ctx, map[string]any{"a": 1})
	col.Patch(ctx, id, v1.MergePatch(map[string]any{"a": 2}))
	col.Delete(ctx, id)
	db.Tx().Put("items", id, map[string]any{}).Commit(ctx)
	col.Query(ctx, v1.Query{Filter: v1.Eq("/a", 1)})
	for _, opts := range [][]v1.SubscribeOption{nil, {v1.WithSSE()}} {
		sub, err := db.Subscribe(ctx, v1.ShapeRequest{Collection: "items"}, opts...)
		if err != nil {
			t.Fatalf("subscribe: %v", err)
		}
		sub.Next(ctx)
		sub.Close()
	}

	const tenant, database, coll, index, key = "t1", "main", "items", "by_a", "k1"
	k := func() string { return v1.NewIdempotencyKey() }
	d.CreateTenant(ctx, k(), v1.CreateTenantRequest{Name: "Acme", ExternalRef: "p1"})
	d.ListTenants(ctx, "", v1.ListOptions{})
	d.GetTenant(ctx, tenant)
	name := "Acme 2"
	d.UpdateTenant(ctx, k(), tenant, v1.UpdateTenantRequest{Name: &name})
	d.DeleteTenant(ctx, k(), tenant)
	d.Usage(ctx, tenant)
	d.CreateDatabase(ctx, k(), tenant, v1.CreateDatabaseRequest{Name: database})
	d.ListDatabases(ctx, tenant, v1.ListOptions{})
	d.GetDatabase(ctx, tenant, database)
	d.UpdateDatabase(ctx, k(), tenant, database, v1.Retention{ShapeLogBytes: 1, ShapeLogAgeSeconds: 1})
	d.DeleteDatabase(ctx, k(), tenant, database)
	d.GetOperation(ctx, "op")
	d.CreateCollection(ctx, k(), tenant, database, v1.CreateCollectionRequest{Name: coll})
	d.ListCollections(ctx, tenant, database, v1.ListOptions{})
	d.GetCollection(ctx, tenant, database, coll)
	advisor := "auto"
	d.UpdateCollection(ctx, k(), tenant, database, coll, v1.UpdateCollectionRequest{Advisor: &advisor})
	d.DeleteCollection(ctx, k(), tenant, database, coll)
	d.CollectionStats(ctx, tenant, database, coll)
	d.ListIndexes(ctx, tenant, database, coll, v1.ListOptions{})
	d.CreateIndex(ctx, k(), tenant, database, coll, v1.IndexSpec{Name: index, Keys: []v1.IndexKey{{Path: "/a"}}})
	d.GetIndex(ctx, tenant, database, coll, index)
	d.PinIndex(ctx, k(), tenant, database, coll, index, true)
	d.DeleteIndex(ctx, k(), tenant, database, coll, index)
	d.IndexDecisions(ctx, tenant, database, coll, v1.ListOptions{})
	d.CreateKey(ctx, k(), tenant, v1.CreateKeyRequest{Name: "server", Grants: []v1.Grant{{Database: "*", Collections: []string{"*"}, Permissions: []string{"read"}}}})
	d.ListKeys(ctx, tenant, v1.ListOptions{})
	d.GetKey(ctx, tenant, key)
	d.RevokeKey(ctx, k(), tenant, key)
	d.RotateKey(ctx, k(), tenant, key, nil)
	d.CreateEdgeKey(ctx, k(), "edge")
	d.ListEdgeKeys(ctx, v1.ListOptions{})
	d.DeleteEdgeKey(ctx, k(), key)
	d.ListAdvisors(ctx, v1.ListOptions{})
	d.ListRegions(ctx, v1.ListOptions{})
	d.ListAudit(ctx, "", v1.ListOptions{})

	mu.Lock()
	defer mu.Unlock()
	if len(bad) > 0 {
		t.Errorf("the client sent requests outside the contract:\n%s", strings.Join(bad, "\n"))
	}
	var missing []string
	for _, r := range routes {
		if !seen[r] {
			missing = append(missing, r)
		}
	}
	slices.Sort(missing)
	if len(missing) > 0 {
		t.Errorf("contract routes the client never calls:\n%s", strings.Join(missing, "\n"))
	}

	if fromOpenAPI {
		if len(codes) == 0 {
			t.Fatal("the pinned OpenAPI declares no error code enum")
		}
		mine := map[string]bool{}
		for _, c := range clientErrorCodes {
			mine[string(c)] = true
		}
		for _, c := range codes {
			if !mine[c] {
				t.Errorf("OpenAPI error code %q has no v1.ErrorCode", c)
			}
			delete(mine, c)
		}
		if len(mine) > 0 {
			t.Errorf("v1.ErrorCodes missing from the OpenAPI enum: %v", slices.Sorted(maps.Keys(mine)))
		}
	}
}
