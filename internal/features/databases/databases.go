package databases

import (
	"encoding/json/v2"
	"errors"
	"net/http"
	"regexp"
	"slices"
	"strings"
	"time"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/notfound"
	sdk "terrascale.tech/sdk"
)

var namePattern = regexp.MustCompile(`^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$`)

type databaseView struct {
	ID        string    `json:"id"`
	Name      string    `json:"name"`
	Status    string    `json:"status"`
	Region    string    `json:"region"`
	CreatedAt time.Time `json:"createdAt"`
}

func view(db core.Database) databaseView {
	return databaseView{ID: db.ID, Name: db.Name, Status: db.Status, Region: db.Region, CreatedAt: db.CreatedAt}
}

// Mount owns the database lifecycle routes for the selected project.
func Mount(mux *http.ServeMux, deps core.Deps) {
	mountBranch(mux, deps)
	mux.HandleFunc("GET /databases", func(w http.ResponseWriter, r *http.Request) { renderPage(w, r, deps, "") })
	mux.HandleFunc("GET /databases/{databaseID}", func(w http.ResponseWriter, r *http.Request) { renderPage(w, r, deps, r.PathValue("databaseID")) })
	mux.HandleFunc("GET /api/v1/dashboard/databases", func(w http.ResponseWriter, r *http.Request) {
		_, project, err := selected(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		list := make([]databaseView, 0, len(project.Databases))
		for _, db := range project.Databases {
			list = append(list, view(db))
		}
		slices.SortFunc(list, func(a, b databaseView) int { return strings.Compare(a.Name, b.Name) })
		writeJSON(w, http.StatusOK, list)
	})
	createDatabase := func(w http.ResponseWriter, r *http.Request) {
		if err := deps.CSRF.Verify(r); err != nil {
			http.Error(w, "invalid CSRF token", http.StatusForbidden)
			return
		}
		p, project, err := selected(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		name := strings.TrimSpace(r.FormValue("name"))
		regionValue := r.FormValue("region")
		_, hasRegion := r.Form["region"]
		if r.URL.Path == "/api/v1/dashboard/databases/create" && !hasRegion {
			regionValue = project.Region
		}
		region, err := sdk.ParseRegionCode(regionValue)
		if !namePattern.MatchString(name) || err != nil {
			http.Error(w, "valid database name and region are required", http.StatusBadRequest)
			return
		}
		db := core.Database{ID: core.NewID("db"), ProjectID: p.ProjectID, Name: name, Region: string(region), Status: "provisioning", CreatedAt: time.Now().UTC(), Documents: make(map[string]map[string][]byte)}
		if deps.Demo {
			db.Status = "ready"
		}
		err = deps.Store.UpdateAs(p, func(a *core.Account) error {
			project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
			if err != nil {
				return err
			}
			org := a.Organizations[p.OrganizationID]
			if !org.CanProvisionResources(p.UserID) {
				return core.ErrForbidden
			}
			for _, existing := range project.Databases {
				if existing.Name == name {
					return core.ErrConflict
				}
			}
			if project.Databases == nil {
				project.Databases = make(map[string]core.Database)
			}
			project.Databases[db.ID] = db
			org.Projects[p.ProjectID] = project
			a.Organizations[p.OrganizationID] = org
			return nil
		})
		if err != nil {
			writeError(w, err)
			return
		}
		if !deps.Demo {
			if deps.SDK == nil {
				setStatus(deps, p, db.ID, "failed")
				http.Error(w, "live data plane unavailable", http.StatusServiceUnavailable)
				return
			}
			client, err := deps.SDK.ForScope(p.OrganizationID, p.ProjectID)
			if err != nil {
				setStatus(deps, p, db.ID, "failed")
				http.Error(w, "live data plane unavailable", http.StatusServiceUnavailable)
				return
			}
			operationID := db.ID
			result, err := client.Databases.CreateDatabase(r.Context(), sdk.CreateDatabaseRequest{Name: name, Placement: []sdk.RegionCode{region}, OperationID: operationID}, sdk.WithIdempotencyKey(operationID))
			if err != nil {
				setStatus(deps, p, db.ID, "failed")
				http.Error(w, "database provisioning failed: "+err.Error(), http.StatusBadGateway)
				return
			}
			if result.Operation != nil {
				db.Status = result.Operation.State
			} else if result.Database != nil {
				db.Status = "ready"
			} else {
				setStatus(deps, p, db.ID, "failed")
				http.Error(w, "database provisioning returned no identity", http.StatusBadGateway)
				return
			}
			if err := setStatus(deps, p, db.ID, db.Status); err != nil {
				writeError(w, err)
				return
			}
		}
		if wantsHTML(r) {
			http.Redirect(w, r, "/databases/"+db.ID, http.StatusSeeOther)
			return
		}
		writeJSON(w, http.StatusCreated, view(db))
	}
	mux.HandleFunc("POST /api/v1/dashboard/databases", createDatabase)
	mux.HandleFunc("POST /api/v1/dashboard/databases/create", createDatabase)
	deleteDatabase := func(w http.ResponseWriter, r *http.Request) {
		if err := deps.CSRF.Verify(r); err != nil {
			http.Error(w, "invalid CSRF token", http.StatusForbidden)
			return
		}
		p, project, err := selected(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		if !canManage(deps, p) {
			writeError(w, core.ErrForbidden)
			return
		}
		id := r.PathValue("databaseID")
		if r.Method == http.MethodPost {
			if err := r.ParseForm(); err != nil {
				http.Error(w, "invalid form", http.StatusBadRequest)
				return
			}
			id = r.FormValue("databaseId")
		}
		db, ok := project.Databases[id]
		if !ok {
			writeError(w, core.ErrNotFound)
			return
		}
		if r.Method == http.MethodPost {
			confirmation := r.FormValue("confirmName")
			if _, hasConfirmName := r.Form["confirmName"]; !hasConfirmName {
				confirmation = strings.TrimSpace(r.FormValue("confirm"))
			}
			if confirmation != db.Name {
				http.Error(w, "type the database name to confirm deletion", http.StatusBadRequest)
				return
			}
		}
		if db.Status == "provisioning" || db.Status == "accepted" || db.Status == "pending" {
			http.Error(w, "database provisioning is in progress", http.StatusConflict)
			return
		}
		if !deps.Demo {
			if deps.SDK == nil {
				http.Error(w, "live data plane unavailable", http.StatusServiceUnavailable)
				return
			}
			client, err := deps.SDK.ForScope(p.OrganizationID, p.ProjectID)
			if err != nil {
				http.Error(w, "live data plane unavailable", http.StatusServiceUnavailable)
				return
			}
			opID := core.NewID("del")
			if _, err := client.Databases.DeleteDatabase(r.Context(), db.Name, sdk.WithIdempotencyKey(opID), sdk.WithDeleteConfirmation(db.Name)); err != nil {
				http.Error(w, "database deletion failed: "+err.Error(), http.StatusBadGateway)
				return
			}
		}
		err = deps.Store.UpdateAs(p, func(a *core.Account) error {
			project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
			if err != nil {
				return err
			}
			if !a.Organizations[p.OrganizationID].CanManage(p.UserID) {
				return core.ErrForbidden
			}
			if _, ok := project.Databases[db.ID]; !ok {
				return core.ErrNotFound
			}
			delete(project.Databases, db.ID)
			org := a.Organizations[p.OrganizationID]
			org.Projects[p.ProjectID] = project
			a.Organizations[p.OrganizationID] = org
			return nil
		})
		if err != nil {
			writeError(w, err)
			return
		}
		if wantsHTML(r) {
			http.Redirect(w, r, "/databases", http.StatusSeeOther)
			return
		}
		w.WriteHeader(http.StatusNoContent)
	}
	mux.HandleFunc("DELETE /api/v1/dashboard/databases/{databaseID}", deleteDatabase)
	mux.HandleFunc("POST /api/v1/dashboard/databases/delete", deleteDatabase)
}

type pageData struct {
	ProjectID    string
	Summary      databaseSummary
	Databases    []databaseView
	Detail       *databaseView
	DetailInfo   detailInfo
	Region       string
	Error        string
	Search       string
	OpenCreate   bool
	CanProvision bool
	CanManage    bool
}

// databaseSummary holds the headline numbers for every database in the
// project, independent of the list search.
type databaseSummary struct {
	Total       int
	Ready       int
	InProgress  int
	Failed      int
	Regions     []string
	Collections int
	Documents   int
}

func summarize(databases map[string]core.Database) databaseSummary {
	var summary databaseSummary
	for _, db := range databases {
		summary.Total++
		switch statusTone(db.Status) {
		case "emerald":
			summary.Ready++
		case "amber":
			summary.InProgress++
		case "rose":
			summary.Failed++
		}
		if db.Region != "" && !slices.Contains(summary.Regions, db.Region) {
			summary.Regions = append(summary.Regions, db.Region)
		}
		summary.Collections += len(db.Documents)
		for _, documents := range db.Documents {
			summary.Documents += len(documents)
		}
	}
	slices.Sort(summary.Regions)
	return summary
}

// statusTone colors a database status: ready, in progress, failed, or other.
func statusTone(status string) string {
	switch status {
	case "ready":
		return "emerald"
	case "provisioning", "accepted", "pending":
		return "amber"
	case "failed":
		return "rose"
	default:
		return "slate"
	}
}

func renderPage(w http.ResponseWriter, r *http.Request, deps core.Deps, detailID string) {
	p, project, err := selected(deps, r)
	if err != nil {
		writeError(w, err)
		return
	}
	a, _ := deps.Store.SnapshotAs(p)
	data := pageData{ProjectID: project.ID, Summary: summarize(project.Databases), Region: project.Region, Error: r.URL.Query().Get("error"), Search: strings.TrimSpace(r.URL.Query().Get("q")), OpenCreate: r.URL.Query().Get("create") == "1"}
	if len(data.Search) > 256 {
		data.Search = data.Search[:256]
	}
	org := a.Organizations[p.OrganizationID]
	data.CanProvision = org.CanProvisionResources(p.UserID)
	data.CanManage = org.CanManage(p.UserID)
	if data.Region == "" {
		data.Region = "GRU"
	}
	for _, db := range project.Databases {
		if data.Search == "" || strings.Contains(strings.ToLower(db.Name), strings.ToLower(data.Search)) {
			data.Databases = append(data.Databases, view(db))
		}
		if db.ID == detailID {
			v := view(db)
			data.Detail = &v
			data.DetailInfo = makeDetailInfo(deps.Demo, p, a, project, db, r.URL.Query().Get("tab"), data.CanManage, data.CanProvision && data.CanManage)
		}
	}
	if detailID != "" && data.Detail == nil {
		notfound.Render(w, r)
		return
	}
	slices.SortFunc(data.Databases, func(a, b databaseView) int { return strings.Compare(a.Name, b.Name) })
	page := core.Page{Layout: "shell", Title: "Databases · TerraScale", Principal: p, View: func(page core.Page) templ.Component { return databasesContent(page, data) }, CSRFToken: deps.CSRF.Token(w, r), Shell: core.Shell{UserName: a.User.Name, UserEmail: a.User.Email, OrganizationName: org.Name, ProjectName: project.Name}}
	if err := deps.Render.Page(w, r, page); err != nil {
		http.Error(w, "could not render databases", http.StatusInternalServerError)
	}
}

func wantsHTML(r *http.Request) bool { return strings.Contains(r.Header.Get("Accept"), "text/html") }

func selected(deps core.Deps, r *http.Request) (core.Principal, core.Project, error) {
	p, ok := deps.Sessions.Principal(r)
	if !ok || p.ProjectID == "" {
		return core.Principal{}, core.Project{}, core.ErrUnauthorized
	}
	a, err := deps.Store.SnapshotAs(p)
	if err != nil {
		return core.Principal{}, core.Project{}, err
	}
	project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
	return p, project, err
}

func setStatus(deps core.Deps, p core.Principal, databaseID, status string) error {
	return deps.Store.UpdateAs(p, func(a *core.Account) error {
		project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
		if err != nil {
			return err
		}
		db, ok := project.Databases[databaseID]
		if !ok {
			return core.ErrNotFound
		}
		db.Status = status
		project.Databases[databaseID] = db
		org := a.Organizations[p.OrganizationID]
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return nil
	})
}

func canManage(deps core.Deps, p core.Principal) bool {
	a, err := deps.Store.SnapshotAs(p)
	return err == nil && a.Organizations[p.OrganizationID].CanManage(p.UserID)
}

func writeError(w http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, core.ErrUnauthorized):
		http.Error(w, "authentication required", http.StatusUnauthorized)
	case errors.Is(err, core.ErrForbidden):
		http.Error(w, "forbidden", http.StatusForbidden)
	case errors.Is(err, core.ErrNotFound):
		http.Error(w, "not found", http.StatusNotFound)
	case errors.Is(err, core.ErrConflict):
		http.Error(w, "conflict", http.StatusConflict)
	default:
		http.Error(w, "invalid request", http.StatusBadRequest)
	}
}

func writeJSON(w http.ResponseWriter, status int, data any) {
	encoded, err := json.Marshal(data)
	if err != nil {
		http.Error(w, "encode response failed", http.StatusInternalServerError)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_, _ = w.Write(encoded)
}
