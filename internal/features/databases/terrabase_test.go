package databases

import (
	"encoding/json"
	"errors"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/terrabasetest/dashboardtest"
	"terrascale.tech/sdk/v1"
)

// TestDatabasesLiveInTerraBase covers production's databases: one created
// in the dashboard is created in a TerraBase project made for the dashboard
// project as the signed-in user, its documents are the ones an API key
// reads and writes there, and deleting it deletes it there.
func TestDatabasesLiveInTerraBase(t *testing.T) {
	d := dashboardtest.Start(t)
	fromDashboard, fromAPI := v1.DocumentIDFor("items", "dashboard"), v1.DocumentIDFor("items", "api")
	ctx := d.WithToken(t.Context())
	service := Service{deps: d.Deps}

	created, err := service.Create(ctx, d.Principal, "orders", "GRU")
	if err != nil {
		t.Fatal(err)
	}
	project := d.Project(t)
	org, proj, _ := d.TerraBase(t)
	db := project.Databases[created.ID]
	if db.ID == "" || len(db.Documents) != 0 {
		t.Fatalf("database = %+v; want TerraBase's ID and no local documents", db)
	}
	m := d.Management()
	if remote, err := m.Database(ctx, org, proj, db.ID); err != nil || remote.Name != "orders" {
		t.Fatalf("TerraBase database = %+v, %v; want orders", remote, err)
	}
	if remote, err := m.Organization(ctx, org); err != nil || remote.Name != d.Principal.OrganizationID {
		t.Fatalf("TerraBase organization = %+v, %v; want one named by the dashboard organization", remote, err)
	}

	// An application's key reads what the dashboard wrote, and the reverse.
	key, err := m.CreateProjectAPIKey(ctx, org, proj, v1.ProjectAPIKeyRequest{Name: "app", Scopes: []string{"documents:read", "documents:write"}})
	if err != nil {
		t.Fatal(err)
	}
	api := d.API(t, key.Secret).Database(proj, "orders")
	dashboard, err := d.Deps.Data(ctx, d.Principal, project, db)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := dashboard.CreateCollection(ctx, "items"); err != nil {
		t.Fatal(err)
	}
	if _, err := dashboard.Collection("items").Put(ctx, fromDashboard, map[string]int{"n": 1}, v1.IfNoneMatchAny()); err != nil {
		t.Fatal(err)
	}
	if doc, err := api.Collection("items").Get(ctx, fromDashboard); err != nil || string(doc.Document) != `{"n":1}` {
		t.Fatalf("API read = %+v, %v", doc, err)
	}
	if _, err := api.Collection("items").Put(ctx, fromAPI, map[string]int{"n": 2}, v1.IfNoneMatchAny()); err != nil {
		t.Fatal(err)
	}
	if doc, err := dashboard.Collection("items").Get(ctx, fromAPI); err != nil || string(doc.Document) != `{"n":2}` {
		t.Fatalf("dashboard read = %+v, %v", doc, err)
	}

	// Past the plan's allowance, TerraBase is not asked for a database.
	if _, err := service.Create(ctx, d.Principal, "second", "GRU"); !errors.Is(err, core.ErrDatabaseLimit) {
		t.Fatalf("second database on Free = %v, want the plan limit", err)
	}
	if list, err := m.Databases(ctx, org, proj, v1.PageOptions{}); err != nil || len(list.Items) != 1 {
		t.Fatalf("TerraBase databases = %+v, %v; want orders only", list, err)
	}
	err = d.Deps.Store.Update(ctx, d.Principal.AccountID, func(a *core.Account) error {
		org := a.Organizations[d.Principal.OrganizationID]
		org.PlanID = "7CNW2H87JE68ATZX224CV2G625"
		a.Organizations[org.ID] = org
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}

	// The second database reuses the project's data key.
	_, _, dataKey := d.TerraBase(t)
	branch, err := service.Branch(ctx, d.Principal, created.ID, "orders-copy")
	if err != nil {
		t.Fatal(err)
	}
	if _, _, again := d.TerraBase(t); again != dataKey || again.Secret == "" {
		t.Fatalf("data key changed to %+v from %+v", again, dataKey)
	}
	copied, err := api.Collection("items").Get(ctx, fromAPI)
	if err != nil {
		t.Fatal(err)
	}
	if doc, err := d.API(t, key.Secret).Database(proj, "orders-copy").Collection("items").Get(ctx, fromAPI); err != nil || !jsonEqual(doc.Document, copied.Document) {
		t.Fatalf("branch document = %+v, %v; want the source's", doc, err)
	}
	detail, err := service.Detail(ctx, d.Principal, created.ID)
	if err != nil || detail.Collections != 1 || detail.Documents != 2 || detail.Unavailable != "" {
		t.Fatalf("detail = %+v, %v; want TerraBase's 1 collection and 2 documents", detail, err)
	}

	if err := service.Delete(ctx, d.Principal, created.ID, "orders"); err != nil {
		t.Fatal(err)
	}
	if _, err := m.Database(ctx, org, proj, db.ID); !errors.Is(err, v1.ErrNotFound) && !errors.Is(err, v1.ErrUnknownRoute) {
		t.Fatalf("deleted database still in TerraBase: %v", err)
	}
	if _, ok := d.Project(t).Databases[branch.ID]; !ok {
		t.Fatal("deleting the source removed its branch")
	}

	// Without the ts-auth token (MCP, the platform API), a project's first
	// database cannot be made in TerraBase.
	other := dashboardtest.Start(t)
	if _, err := (Service{deps: other.Deps}).Create(t.Context(), other.Principal, "orders", "GRU"); err == nil {
		t.Fatal("created a database in TerraBase without the user's ts-auth token")
	}
}

func jsonEqual(a, b []byte) bool {
	var x, y any
	return json.Unmarshal(a, &x) == nil && json.Unmarshal(b, &y) == nil && string(mustJSON(x)) == string(mustJSON(y))
}

func mustJSON(v any) []byte {
	b, _ := json.Marshal(v)
	return b
}
