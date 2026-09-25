package databases

import (
	json "encoding/json/v2"
	"errors"
	"net/http"
	"net/url"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

type rejectingCSRF struct{ testCSRF }

func (rejectingCSRF) Verify(*http.Request) error { return errors.New("invalid CSRF token") }

func TestLegacyCreateUsesValidatedProjectRegionOnlyWhenRegionOmitted(t *testing.T) {
	deps, p := databaseFixture(t, "owner")
	if err := deps.Store.UpdateAs(p, func(a *core.Account) error {
		org := a.Organizations[p.OrganizationID]
		project := org.Projects[p.ProjectID]
		project.Region = "SFO"
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	mux := http.NewServeMux()
	Mount(mux, deps)
	created := post(mux, "/api/v1/dashboard/databases/create", url.Values{"name": {"legacy"}})
	if created.Code != http.StatusCreated {
		t.Fatalf("legacy create status %d: %s", created.Code, created.Body.String())
	}
	var database databaseView
	if err := json.Unmarshal(created.Body.Bytes(), &database); err != nil {
		t.Fatal(err)
	}
	if database.Region != "SFO" {
		t.Fatalf("legacy region = %q, want selected project SFO", database.Region)
	}
	for _, tc := range []struct {
		name, path string
		form       url.Values
	}{
		{"current requires region", "/api/v1/dashboard/databases", url.Values{"name": {"current"}}},
		{"legacy explicit blank region", "/api/v1/dashboard/databases/create", url.Values{"name": {"blank"}, "region": {""}}},
		{"legacy invalid region", "/api/v1/dashboard/databases/create", url.Values{"name": {"invalid"}, "region": {"BAD"}}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			w := post(mux, tc.path, tc.form)
			if w.Code != http.StatusBadRequest {
				t.Fatalf("create status = %d, want 400: %s", w.Code, w.Body.String())
			}
		})
	}
	if err := deps.Store.UpdateAs(p, func(a *core.Account) error {
		org := a.Organizations[p.OrganizationID]
		project := org.Projects[p.ProjectID]
		project.Region = "BAD"
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	if w := post(mux, "/api/v1/dashboard/databases/create", url.Values{"name": {"bad-default"}}); w.Code != http.StatusBadRequest {
		t.Fatalf("legacy create with invalid selected region status = %d, want 400", w.Code)
	}

	deps.CSRF = rejectingCSRF{}
	protected := http.NewServeMux()
	Mount(protected, deps)
	if w := post(protected, "/api/v1/dashboard/databases/create", url.Values{"name": {"rejected"}}); w.Code != http.StatusForbidden {
		t.Fatalf("legacy create with invalid CSRF status = %d, want 403", w.Code)
	}
	memberDeps, _ := databaseFixture(t, "member")
	memberMux := http.NewServeMux()
	Mount(memberMux, memberDeps)
	if w := post(memberMux, "/api/v1/dashboard/databases/create", url.Values{"name": {"denied"}}); w.Code != http.StatusForbidden {
		t.Fatalf("member legacy create status = %d, want 403", w.Code)
	}
}

func TestLegacyDeleteConfirmationCannotBypassCurrentNameOrScope(t *testing.T) {
	deps, p := databaseFixture(t, "owner")
	if err := deps.Store.UpdateAs(p, func(a *core.Account) error {
		org := a.Organizations[p.OrganizationID]
		project := org.Projects[p.ProjectID]
		project.Databases["db-owned"] = core.Database{ID: "db-owned", Name: "owned", ProjectID: p.ProjectID, Status: "ready"}
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	other := core.Account{
		ID: "acct-other", User: core.User{ID: "user-other", Email: "other@example.test"},
		ActiveOrganizationID: "org-other", ActiveProjectID: "project-other",
		Organizations: map[string]core.Organization{
			"org-other": {ID: "org-other", OwnerUserID: "user-other", Projects: map[string]core.Project{
				"project-other": {ID: "project-other", Databases: map[string]core.Database{
					"db-foreign": {ID: "db-foreign", Name: "foreign", ProjectID: "project-other", Status: "ready"},
				}},
			}},
		},
	}
	if err := deps.Store.InsertAccount(other); err != nil {
		t.Fatal(err)
	}
	mux := http.NewServeMux()
	Mount(mux, deps)
	for _, tc := range []struct {
		name string
		form url.Values
		want int
	}{
		{"foreign database", url.Values{"databaseId": {"db-foreign"}, "confirm": {"foreign"}}, http.StatusNotFound},
		{"wrong legacy confirmation", url.Values{"databaseId": {"db-owned"}, "confirm": {"wrong"}}, http.StatusBadRequest},
		{"current confirmation takes priority", url.Values{"databaseId": {"db-owned"}, "confirm": {"owned"}, "confirmName": {"wrong"}}, http.StatusBadRequest},
		{"legacy confirmation", url.Values{"databaseId": {"db-owned"}, "confirm": {" owned "}}, http.StatusNoContent},
	} {
		t.Run(tc.name, func(t *testing.T) {
			w := post(mux, "/api/v1/dashboard/databases/delete", tc.form)
			if w.Code != tc.want {
				t.Fatalf("delete status = %d, want %d: %s", w.Code, tc.want, w.Body.String())
			}
		})
	}
}

func TestLegacyBranchDefaultsNameAndRejectsCollision(t *testing.T) {
	deps, p := databaseFixture(t, "owner")
	if err := deps.Store.UpdateAs(p, func(a *core.Account) error {
		org := a.Organizations[p.OrganizationID]
		project := org.Projects[p.ProjectID]
		project.Databases["db-source"] = core.Database{ID: "db-source", Name: "primary", ProjectID: p.ProjectID, Region: "GRU", Status: "ready"}
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	mux := http.NewServeMux()
	Mount(mux, deps)
	branched := post(mux, "/api/v1/dashboard/databases/branch", url.Values{"databaseId": {"db-source"}})
	if branched.Code != http.StatusCreated {
		t.Fatalf("legacy branch status %d: %s", branched.Code, branched.Body.String())
	}
	var branch databaseView
	if err := json.Unmarshal(branched.Body.Bytes(), &branch); err != nil {
		t.Fatal(err)
	}
	if branch.Name != "primary-preview-branch" || branch.Region != "GRU" {
		t.Fatalf("legacy branch = %+v, want deterministic name and source region", branch)
	}
	for _, tc := range []struct {
		name string
		form url.Values
		want int
	}{
		{"collision", url.Values{"databaseId": {"db-source"}}, http.StatusConflict},
		{"explicit blank", url.Values{"databaseId": {"db-source"}, "name": {""}}, http.StatusBadRequest},
		{"foreign source", url.Values{"databaseId": {"db-foreign"}}, http.StatusNotFound},
		{"current explicit name", url.Values{"databaseId": {"db-source"}, "name": {"named-branch"}}, http.StatusCreated},
	} {
		t.Run(tc.name, func(t *testing.T) {
			w := post(mux, "/api/v1/dashboard/databases/branch", tc.form)
			if w.Code != tc.want {
				t.Fatalf("branch status = %d, want %d: %s", w.Code, tc.want, w.Body.String())
			}
		})
	}
	if err := deps.Store.UpdateAs(p, func(a *core.Account) error {
		org := a.Organizations[p.OrganizationID]
		project := org.Projects[p.ProjectID]
		project.Databases["db-long"] = core.Database{ID: "db-long", Name: strings.Repeat("a", 63), ProjectID: p.ProjectID, Region: "GRU", Status: "ready"}
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	long := post(mux, "/api/v1/dashboard/databases/branch", url.Values{"databaseId": {"db-long"}})
	if long.Code != http.StatusCreated {
		t.Fatalf("long source branch status %d: %s", long.Code, long.Body.String())
	}
	if err := json.Unmarshal(long.Body.Bytes(), &branch); err != nil {
		t.Fatal(err)
	}
	if len(branch.Name) > 63 || !namePattern.MatchString(branch.Name) || !strings.HasSuffix(branch.Name, "-preview-branch") {
		t.Fatalf("long source default branch name %q is not valid and bounded", branch.Name)
	}
}
