package data

import (
	"errors"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/platform/terrabasetest"
	"terrascale.tech/sdk/v1"
)

// TestSystemDatabaseReadsLiveTerraBaseWithSecretsMasked covers a platform
// admin browsing a system database: collections, documents and SQL come from
// TerraBase through Management with the admin's ts-auth token, secrets are
// masked, and nothing can be changed.
func TestSystemDatabaseReadsLiveTerraBaseWithSecretsMasked(t *testing.T) {
	tb := terrabasetest.Start(t)
	store := memstore.New()
	system := terrabasetest.SystemDatabase(t, tb, store)
	ctx := t.Context()
	for _, name := range []string{"users", "sessions", "route-tables"} {
		if _, err := system.Documents.CreateCollection(ctx, name); err != nil {
			t.Fatal(err)
		}
	}
	users := system.Documents.Collection("users")
	ana, err := users.Create(ctx, map[string]any{"email": "ana@example.com", "passwordHash": "$argon2id$secret", "profile": map[string]any{"refresh_token": "rt-secret", "name": "Ana"}})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := users.Create(ctx, map[string]any{"email": "bia@example.com"}); err != nil {
		t.Fatal(err)
	}
	service := Service{deps: core.Deps{Store: store, Management: system.Management}}
	p, db := system.Principal, system.Database.ID
	live := system.WithToken(ctx)

	names, err := service.Collections(live, p, db)
	if err != nil || strings.Join(names, ",") != "route-tables,sessions,users" {
		t.Fatalf("collections = %v, %v; want route-tables,sessions,users", names, err)
	}
	if _, err := system.Documents.Collection("route-tables").Create(ctx, map[string]any{"host": "api"}); err != nil {
		t.Fatal(err)
	}
	if routes, _, err := service.Browse(live, p, db, "route-tables", documentSort{Field: "host-name"}); err != nil || len(routes) != 1 {
		t.Fatalf("browse route-tables = %v, %v; want its document", routes, err)
	}
	docs, more, err := service.Browse(live, p, db, "users", documentSort{Field: "email", Desc: true})
	if err != nil || more || len(docs) != 2 || !strings.Contains(docs[0].Value.String(), "bia@example.com") {
		t.Fatalf("browse = %v more=%v, %v; want both users, bia first", docs, more, err)
	}
	doc, err := service.Document(live, p, db, "users", ana.ID)
	if err != nil {
		t.Fatal(err)
	}
	body := doc.Value.String()
	if strings.Contains(body, "argon2id") || strings.Contains(body, "rt-secret") || !strings.Contains(body, core.Masked) || !strings.Contains(body, `"name":"Ana"`) {
		t.Fatalf("document %s: want secrets masked and the rest shown", body)
	}
	if page, err := service.Documents(live, p, db, documentQuery{Collection: "users", Limit: 1}); err != nil || len(page) != 1 {
		t.Fatalf("documents page = %v, %v", page, err)
	}
	run, err := service.RunSQL(live, p, db, "SELECT * FROM users LIMIT 10")
	if err != nil || run.Table.Matched != 2 || strings.Contains(string(run.JSON), "argon2id") {
		t.Fatalf("sql = %+v, %v; want two masked rows", run.Table, err)
	}
	if cut, err := service.RunSQL(live, p, db, "SELECT * FROM users LIMIT 1"); err != nil || len(cut.Table.Rows) != 1 || !cut.Table.More {
		t.Fatalf("sql with a limit = %+v, %v; want one row and more matched", cut.Table, err)
	}
	for _, statement := range []string{"SELECT passwordHash AS email FROM users LIMIT 1", "SELECT * FROM users WHERE passwordHash > 'a'", "SELECT * FROM users ORDER BY passwordHash"} {
		if _, err := service.RunSQL(live, p, db, statement); !errors.Is(err, core.ErrInvalid) {
			t.Fatalf("%s = %v, want refused so secrets stay masked", statement, err)
		}
	}
	if _, err := service.Document(live, p, db, "users", v1.DocumentIDFor("users", "missing")); !errors.Is(err, core.ErrNotFound) {
		t.Fatalf("missing document: %v, want not found", err)
	}

	if _, err := service.WriteDocument(live, p, db, "users", "patch", ana.ID, []byte(`{"email":"x@example.com"}`)); !errors.Is(err, core.ErrSystemReadOnly) {
		t.Fatalf("write = %v, want read-only", err)
	}
	if err := service.CreateCollection(live, p, db, "extra"); !errors.Is(err, core.ErrSystemReadOnly) {
		t.Fatalf("create collection = %v, want read-only", err)
	}
	if _, err := service.Collections(ctx, p, db); !errors.Is(err, core.ErrDataPlane) {
		t.Fatalf("without a ts-auth token = %v, want unavailable", err)
	}
	member := p
	member.PlatformAdmin = false
	if _, _, err := service.Browse(live, member, db, "users", documentSort{}); !errors.Is(err, core.ErrForbidden) {
		t.Fatalf("non-admin browse = %v, want forbidden", err)
	}
}
