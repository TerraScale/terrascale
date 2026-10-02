package v1_test

// Fast-suite integration tests for the /v1/dashboard control-plane client
// against the contract fixture (fixture_test.go).

import (
	"encoding/json"
	"errors"
	"strings"
	"testing"
	"time"

	v1 "terrascale.tech/sdk/v1"
)

func TestDashboardProvisioningWorkflow(t *testing.T) {
	t.Parallel()
	f := newFixture(t)
	ctx := t.Context()
	d, err := v1.NewDashboard(v1.Config{BaseURL: f.origin.URL, Token: f.controlToken, HTTPClient: f.httpClient()})
	if err != nil {
		t.Fatal(err)
	}
	must := func(err error) {
		t.Helper()
		if err != nil {
			t.Fatal(err)
		}
	}

	// Mutations require an Idempotency-Key; a retry with the same key
	// returns the original response.
	if _, err := d.CreateTenant(ctx, "", v1.CreateTenantRequest{Name: "x", ExternalRef: "x"}); err == nil || f.count("POST", "/v1/dashboard/tenants") != 0 {
		t.Fatalf("missing key must fail locally: %v", err)
	}
	key := v1.NewIdempotencyKey()
	tenant, err := d.CreateTenant(ctx, key, v1.CreateTenantRequest{Name: "Shop", ExternalRef: "proj_shop"})
	must(err)
	again, err := d.CreateTenant(ctx, key, v1.CreateTenantRequest{Name: "Shop", ExternalRef: "proj_shop"})
	if err != nil || again.ID != tenant.ID || tenant.Status != "active" || tenant.Limits.MaxStorageBytes != 10737418240 {
		t.Fatalf("idempotent create: %+v %+v %v", tenant, again, err)
	}
	_, err = d.CreateTenant(ctx, "other-key", v1.CreateTenantRequest{Name: "Shop", ExternalRef: "proj_shop"})
	requireCode(t, err, v1.ErrNameTaken)

	found, err := d.ListTenants(ctx, "proj_shop", v1.ListOptions{})
	if err != nil || len(found.Items) != 1 || found.Items[0].ID != tenant.ID || found.NextCursor != nil {
		t.Fatalf("list by external_ref: %+v %v", found, err)
	}
	suspended := "suspended"
	updated, err := d.UpdateTenant(ctx, v1.NewIdempotencyKey(), tenant.ID, v1.UpdateTenantRequest{Status: &suspended})
	if err != nil || updated.Status != "suspended" {
		t.Fatalf("suspend: %+v %v", updated, err)
	}
	active := "active"
	_, err = d.UpdateTenant(ctx, v1.NewIdempotencyKey(), tenant.ID, v1.UpdateTenantRequest{Status: &active})
	must(err)

	// Databases, with list pagination.
	for _, name := range []string{"main", "logs"} {
		_, err := d.CreateDatabase(ctx, v1.NewIdempotencyKey(), tenant.ID, v1.CreateDatabaseRequest{Name: name, HomeRegion: "gru"})
		must(err)
	}
	first, err := d.ListDatabases(ctx, tenant.ID, v1.ListOptions{Limit: 1})
	if err != nil || len(first.Items) != 1 || first.NextCursor == nil {
		t.Fatalf("first page: %+v %v", first, err)
	}
	second, err := d.ListDatabases(ctx, tenant.ID, v1.ListOptions{Limit: 1, Cursor: *first.NextCursor})
	if err != nil || len(second.Items) != 1 || second.Items[0].Name != "logs" || second.NextCursor != nil {
		t.Fatalf("second page: %+v %v", second, err)
	}
	main, err := d.GetDatabase(ctx, tenant.ID, "main")
	if err != nil || main.Status != "ready" || main.Retention.ShapeLogBytes != 1073741824 || main.Durability != v1.LocalDurable {
		t.Fatalf("database: %+v %v", main, err)
	}
	_, err = d.UpdateDatabase(ctx, v1.NewIdempotencyKey(), tenant.ID, "main", v1.Retention{ShapeLogBytes: 1 << 20, ShapeLogAgeSeconds: 3600})
	must(err)

	// Collections with constraints, declared indexes and statistics.
	users, err := d.CreateCollection(ctx, v1.NewIdempotencyKey(), tenant.ID, "main", v1.CreateCollectionRequest{
		Name:    "users",
		Schema:  &v1.Schema{Fields: []v1.Field{{Name: "email", Type: "utf8", Required: true}, {Name: "tags", Type: "array", Element: &v1.Element{Type: "utf8"}}}},
		Unique:  []v1.UniqueConstraint{{Name: "email", Paths: []string{"/email"}, Transform: "lower"}},
		Indexes: []v1.IndexSpec{{Name: "by_created", Keys: []v1.IndexKey{{Path: "/created_at", Order: "desc"}}}},
	})
	if err != nil || users.Generation != 1 || users.Schema == nil || len(users.Unique) != 1 || users.Advisor != "auto" {
		t.Fatalf("collection: %+v %v", users, err)
	}
	ttl := "/expires_at"
	_, err = d.UpdateCollection(ctx, v1.NewIdempotencyKey(), tenant.ID, "main", "users", v1.UpdateCollectionRequest{TTLPath: &ttl})
	must(err)
	got, err := d.GetCollection(ctx, tenant.ID, "main", "users")
	if err != nil || got.TTLPath == nil || *got.TTLPath != ttl {
		t.Fatalf("ttl update: %+v %v", got, err)
	}
	stats, err := d.CollectionStats(ctx, tenant.ID, "main", "users")
	if err != nil || len(stats.Paths) != 1 || stats.Paths[0].Types["utf8"] != 0.99 {
		t.Fatalf("stats: %+v %v", stats, err)
	}
	colls, err := d.ListCollections(ctx, tenant.ID, "main", v1.ListOptions{})
	if err != nil || len(colls.Items) != 1 {
		t.Fatalf("collections: %+v %v", colls, err)
	}

	ix, err := d.CreateIndex(ctx, v1.NewIdempotencyKey(), tenant.ID, "main", "users", v1.IndexSpec{Name: "by_name", Keys: []v1.IndexKey{{Path: "/name", Order: "asc", Transform: "lower"}}})
	if err != nil || ix.State != "backfilling" || !ix.Pinned || ix.Progress == nil || ix.Progress.TotalEstimate != 100 {
		t.Fatalf("create index: %+v %v", ix, err)
	}
	pinned, err := d.PinIndex(ctx, v1.NewIdempotencyKey(), tenant.ID, "main", "users", "by_name", false)
	if err != nil || pinned.Pinned {
		t.Fatalf("unpin: %+v %v", pinned, err)
	}
	indexes, err := d.ListIndexes(ctx, tenant.ID, "main", "users", v1.ListOptions{})
	if err != nil || len(indexes.Items) != 3 {
		t.Fatalf("indexes: %+v %v", indexes, err)
	}
	_, err = d.DeleteIndex(ctx, v1.NewIdempotencyKey(), tenant.ID, "main", "users", "unique_email")
	requireCode(t, err, v1.ErrIndexProtected)
	_, err = d.DeleteIndex(ctx, v1.NewIdempotencyKey(), tenant.ID, "main", "users", "by_name")
	must(err)
	_, err = d.GetIndex(ctx, tenant.ID, "main", "users", "by_name")
	requireCode(t, err, v1.ErrNotFound)
	decisions, err := d.IndexDecisions(ctx, tenant.ID, "main", "users", v1.ListOptions{})
	if err != nil || len(decisions.Items) != 1 || !json.Valid(decisions.Items[0]) {
		t.Fatalf("index decisions: %+v %v", decisions, err)
	}

	// A data key created here works on the data plane; rotation issues a
	// working replacement and revocation takes effect immediately.
	created, err := d.CreateKey(ctx, v1.NewIdempotencyKey(), tenant.ID, v1.CreateKeyRequest{Name: "server",
		Grants: []v1.Grant{{Database: "main", Collections: []string{"users"}, Permissions: []string{"read", "write"}}}})
	if err != nil || created.Key.Role != "data" || !strings.HasPrefix(created.Token, "tbk1.") || created.Key.LastFour != created.Token[len(created.Token)-4:] {
		t.Fatalf("create key: %+v %v", created, err)
	}
	data := newClient(t, f, created.Token).Database("main")
	id := v1.DocumentIDFor("users", "ana")
	_, err = data.Collection("users").Put(ctx, id, map[string]any{"email": "ana@example.com"})
	must(err)
	_, err = data.Collection("other").Get(ctx, id)
	requireCode(t, err, v1.ErrForbiddenScope)

	grace := 0
	rotated, err := d.RotateKey(ctx, v1.NewIdempotencyKey(), tenant.ID, created.Key.ID, &grace)
	if err != nil || rotated.Key.ID == created.Key.ID {
		t.Fatalf("rotate: %+v %v", rotated, err)
	}
	rotatedUsers := newClient(t, f, rotated.Token).Database("main").Collection("users")
	_, err = rotatedUsers.Get(ctx, id)
	must(err)
	time.Sleep(5 * time.Millisecond)
	_, err = data.Collection("users").Get(ctx, id)
	requireCode(t, err, v1.ErrInvalidKey)
	revoked, err := d.RevokeKey(ctx, v1.NewIdempotencyKey(), tenant.ID, rotated.Key.ID)
	if err != nil || revoked.RevokedAt == nil {
		t.Fatalf("revoke: %+v %v", revoked, err)
	}
	_, err = rotatedUsers.Get(ctx, id)
	requireCode(t, err, v1.ErrInvalidKey)
	keys, err := d.ListKeys(ctx, tenant.ID, v1.ListOptions{})
	if err != nil || len(keys.Items) != 2 {
		t.Fatalf("keys: %+v %v", keys, err)
	}
	if k, err := d.GetKey(ctx, tenant.ID, created.Key.ID); err != nil || k.ExpiresAt == nil {
		t.Fatalf("rotated key expiry: %+v %v", k, err)
	}

	edge, err := d.CreateEdgeKey(ctx, v1.NewIdempotencyKey(), "worker")
	if err != nil || edge.Key.Role != "edge" {
		t.Fatalf("edge key: %+v %v", edge, err)
	}
	edges, err := d.ListEdgeKeys(ctx, v1.ListOptions{})
	if err != nil || len(edges.Items) != 2 { // the fixture's own edge key plus this one
		t.Fatalf("edge keys: %+v %v", edges, err)
	}
	_, err = d.DeleteEdgeKey(ctx, v1.NewIdempotencyKey(), edge.Key.ID)
	must(err)

	advisors, err := d.ListAdvisors(ctx)
	if err != nil || len(advisors) != 2 || advisors[1].RestUntil == nil || advisors[0].LastLatencyMS != 420 {
		t.Fatalf("advisors: %+v %v", advisors, err)
	}
	regions, err := d.ListRegions(ctx)
	if err != nil || len(regions) != 1 || regions[0].Name != "São Paulo" {
		t.Fatalf("regions: %+v %v", regions, err)
	}
	usage, err := d.Usage(ctx, tenant.ID)
	if err != nil || usage.Databases != 2 || usage.Documents != 1 {
		t.Fatalf("usage: %+v %v", usage, err)
	}

	// Asynchronous deletion with WaitOperation.
	op, err := d.DeleteDatabase(ctx, v1.NewIdempotencyKey(), tenant.ID, "logs")
	if err != nil || op.Status != v1.OperationRunning {
		t.Fatalf("delete database: %+v %v", op, err)
	}
	done, err := d.WaitOperation(ctx, op.ID, 10*time.Millisecond)
	if err != nil || done.Status != v1.OperationSucceeded || done.FinishedAt == nil {
		t.Fatalf("wait: %+v %v", done, err)
	}

	op, err = d.DeleteCollection(ctx, v1.NewIdempotencyKey(), tenant.ID, "main", "users")
	if err != nil || op.Kind != "delete_collection" {
		t.Fatalf("delete collection: %+v %v", op, err)
	}

	audit, err := d.ListAudit(ctx, tenant.ID, v1.ListOptions{Limit: 200})
	if err != nil || len(audit.Items) < 10 || audit.Items[0].Action == "" || audit.Items[len(audit.Items)-1].IdempotencyKey != key {
		t.Fatalf("audit: %d items %v", len(audit.Items), err)
	}

	// The data key cannot use the control plane, and vice versa.
	dataDash, err := v1.NewDashboard(v1.Config{BaseURL: f.origin.URL, Token: f.dataToken, HTTPClient: f.httpClient()})
	must(err)
	_, err = dataDash.ListRegions(ctx)
	requireCode(t, err, v1.ErrForbiddenScope)
	var apiErr *v1.APIError
	if _, err := d.GetTenant(ctx, "../operations"); err == nil || errors.As(err, &apiErr) {
		t.Fatalf("path parameters must be validated locally: %v", err)
	}
}
