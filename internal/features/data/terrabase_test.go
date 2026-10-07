package data

import (
	"errors"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/terrabasetest/dashboardtest"
	"terrascale.tech/sdk/v1"
)

// TestDocumentsLiveInTerraBase covers production's data pages: collections,
// documents, SQL and indexes of a dashboard database are TerraBase's, so an
// application's API key sees what the dashboard writes, and the reverse.
func TestDocumentsLiveInTerraBase(t *testing.T) {
	d := dashboardtest.Start(t)
	ctx := d.WithToken(t.Context())
	// A phase-1 record (TS-472): in the dashboard, never used in TerraBase.
	db := core.Database{ID: core.NewID("db"), ProjectID: d.Principal.ProjectID, Name: "shop", Region: "GRU", Status: "ready"}
	err := d.Deps.Store.UpdateAs(ctx, d.Principal, func(a *core.Account) error {
		a.Organizations[d.Principal.OrganizationID].Projects[d.Principal.ProjectID].Databases[db.ID] = db
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}
	service := Service{deps: d.Deps}

	// The first use creates the database in TerraBase.
	if err := service.CreateCollection(ctx, d.Principal, db.ID, "items"); err != nil {
		t.Fatal(err)
	}
	created, err := service.WriteDocument(ctx, d.Principal, db.ID, "items", "create", "", []byte(`{"name":"lamp","price":30}`))
	if err != nil {
		t.Fatal(err)
	}
	org, proj, _ := d.TerraBase(t)
	key, err := d.Management().CreateProjectAPIKey(ctx, org, proj, v1.ProjectAPIKeyRequest{Name: "app", Scopes: []string{"documents:read", "documents:write"}})
	if err != nil {
		t.Fatal(err)
	}
	api := d.API(t, key.Secret).Database(proj, "shop").Collection("items")
	if doc, err := api.Get(ctx, created.ID); err != nil || !strings.Contains(string(doc.Document), `"lamp"`) {
		t.Fatalf("API read of the dashboard's document = %+v, %v", doc, err)
	}
	fromAPI, err := api.Create(ctx, map[string]any{"name": "desk", "price": 200})
	if err != nil {
		t.Fatal(err)
	}
	if doc, err := service.Document(ctx, d.Principal, db.ID, "items", fromAPI.ID); err != nil || !strings.Contains(doc.Value.String(), `"desk"`) {
		t.Fatalf("dashboard read of the API's document = %+v, %v", doc, err)
	}
	if _, err := service.WriteDocument(ctx, d.Principal, db.ID, "items", "patch", fromAPI.ID, []byte(`{"price":180}`)); err != nil {
		t.Fatal(err)
	}
	if doc, err := api.Get(ctx, fromAPI.ID); err != nil || !strings.Contains(string(doc.Document), `"price":180`) {
		t.Fatalf("API read after the dashboard's patch = %+v, %v", doc, err)
	}
	if _, err := service.WriteDocument(ctx, d.Principal, db.ID, "items", "create", "not-a-uuid", []byte(`{}`)); !errors.Is(err, core.ErrInvalid) {
		t.Fatalf("an ID TerraBase refuses = %v, want invalid input", err)
	}

	if names, err := service.Collections(ctx, d.Principal, db.ID); err != nil || strings.Join(names, ",") != "items" {
		t.Fatalf("collections = %v, %v", names, err)
	}
	if docs, more, err := service.Browse(ctx, d.Principal, db.ID, "items", documentSort{Field: "price", Desc: true}); err != nil || more || len(docs) != 2 || !strings.Contains(docs[0].Value.String(), "desk") {
		t.Fatalf("browse = %v more=%v, %v; want desk first", docs, more, err)
	}
	run, err := service.RunSQL(ctx, d.Principal, db.ID, "SELECT * FROM items WHERE price > 100")
	if err != nil || run.Table.Matched != 1 || !strings.Contains(run.JSON.String(), "desk") {
		t.Fatalf("SQL = %+v, %v; want the desk", run, err)
	}

	if err := service.CreateIndex(ctx, d.Principal, db.ID, "items", "price"); err != nil {
		t.Fatal(err)
	}
	if err := service.CreateIndex(ctx, d.Principal, db.ID, "items", "price"); err != nil {
		t.Fatalf("existing index = %v, want it kept", err)
	}
	if indexes, err := service.Indexes(ctx, d.Principal, db.ID, "items"); err != nil || len(indexes) != 1 || indexes[0].Field != "price" {
		t.Fatalf("indexes = %+v, %v; want price", indexes, err)
	}
	if err := service.SetSchema(ctx, d.Principal, db.ID, "items", []byte(`{"name":"items"}`)); !errors.Is(err, errNoSchemas) {
		t.Fatalf("schema = %v, want refused outside the demo", err)
	}
	if stored := d.Project(t).Databases[db.ID]; stored.TerraBaseID == "" || len(stored.Documents)+len(stored.Indexes)+len(stored.Schemas) != 0 {
		t.Fatalf("record = %+v; want TerraBase's ID and no local contents", stored)
	}
}
