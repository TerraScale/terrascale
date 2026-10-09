package terrabase

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	v1 "terrascale.tech/sdk/v1"
)

func TestMirrorOrganizationsPreservesDashboardOwnership(t *testing.T) {
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Name: "Admin", Email: "admin@example.test", PasswordHash: "unused", PlatformAdmin: true})
	if err != nil {
		t.Fatal(err)
	}
	orgID, projectID := account.ActiveOrganizationID, account.ActiveProjectID
	dbID, addedDBID, serviceID, serviceDBID := core.NewID(), core.NewID(), core.NewID(), core.NewID()
	laterServiceDBID := core.NewID()
	if err := store.Update(t.Context(), account.ID, func(a *core.Account) error {
		org := a.Organizations[orgID]
		org.PlanID, org.Kind = "09XCWRH05BWWBADSBPTPDMFA7D", "business"
		project := org.Projects[projectID]
		project.Databases[dbID] = core.Database{ID: dbID, ProjectID: projectID, Name: "app", Documents: map[string]map[string][]byte{"orders": {"order": []byte(`{"total":42}`)}}}
		org.Projects[projectID] = project
		a.Organizations[orgID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	remote := v1.Organization{ID: orgID, Name: "Acme", CreatedBy: account.User.ID}
	projects := []v1.Project{
		{ID: projectID, OrganizationID: orgID, Name: "App"},
		{ID: serviceID, OrganizationID: orgID, Name: "ts-auth"},
	}
	databases := map[string][]v1.ManagedDatabase{
		projectID: {{ID: dbID, ProjectID: projectID, Name: "app"}, {ID: addedDBID, ProjectID: projectID, Name: "new-app"}},
		serviceID: {{ID: serviceDBID, ProjectID: serviceID, Name: "auth"}},
	}
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		var response any
		switch r.URL.Path {
		case "/v1/management/organizations":
			response = v1.Page[v1.Organization]{Items: []v1.Organization{remote}}
		case "/v1/management/organizations/" + orgID + "/projects":
			response = v1.Page[v1.Project]{Items: projects}
		case "/v1/management/organizations/" + orgID + "/projects/" + projectID + "/databases":
			response = v1.Page[v1.ManagedDatabase]{Items: databases[projectID]}
		case "/v1/management/organizations/" + orgID + "/projects/" + serviceID + "/databases":
			response = v1.Page[v1.ManagedDatabase]{Items: databases[serviceID]}
		default:
			t.Errorf("unexpected request: %s", r.URL)
			http.NotFound(w, r)
			return
		}
		if err := json.NewEncoder(w).Encode(response); err != nil {
			t.Error(err)
		}
	}))
	t.Cleanup(server.Close)
	management, err := v1.NewManagement(v1.Config{BaseURL: server.URL, Token: strings.Repeat("x", 32), HTTPClient: server.Client()})
	if err != nil {
		t.Fatal(err)
	}
	for iteration := range 2 {
		if iteration == 1 {
			databases[serviceID] = append(databases[serviceID], v1.ManagedDatabase{ID: laterServiceDBID, ProjectID: serviceID, Name: "new-service"})
		}
		if err := MirrorOrganizations(t.Context(), store, management.As(account.User.ID), account.User.Email); err != nil {
			t.Fatal(err)
		}
		got, err := store.FindByEmail(t.Context(), account.User.Email)
		if err != nil {
			t.Fatal(err)
		}
		org := got.Organizations[orgID]
		app, service := org.Projects[projectID], org.Projects[serviceID]
		if app.System || core.SystemDatabase(app.Databases[dbID]) || core.SystemDatabase(app.Databases[addedDBID]) {
			t.Fatalf("dashboard-owned resources became system-owned: %+v", app)
		}
		if string(app.Databases[dbID].Documents["orders"]["order"]) != `{"total":42}` {
			t.Fatal("mirroring changed application data")
		}
		if !service.System || !core.SystemDatabase(service.Databases[serviceDBID]) {
			t.Fatalf("imported service resources lack system ownership: %+v", service)
		}
		if iteration == 1 && !core.SystemDatabase(service.Databases[laterServiceDBID]) {
			t.Fatal("a database discovered on a later mirror lost its service ownership")
		}
	}
}
