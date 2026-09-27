package databases

import (
	"cmp"
	"errors"
	"net/http"
	"slices"
	"strings"
	"time"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	sdk "terrascale.tech/sdk"
)

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
	mux.HandleFunc("GET /databases", func(w http.ResponseWriter, r *http.Request) { renderList(w, r, deps) })
	mountDetail(mux, deps)
	mux.HandleFunc("GET /api/v1/dashboard/databases", func(w http.ResponseWriter, r *http.Request) {
		_, _, project, err := selected(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		list := make([]databaseView, 0, len(project.Databases))
		for _, db := range project.Databases {
			list = append(list, view(db))
		}
		slices.SortFunc(list, func(a, b databaseView) int { return strings.Compare(a.Name, b.Name) })
		core.WriteJSON(w, http.StatusOK, list)
	})
	createDatabase := func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		p, _, project, err := selected(deps, r)
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
		if !core.ValidDatabaseName(name) || err != nil {
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
		if core.WantsHTML(r) {
			http.Redirect(w, r, "/databases/"+db.ID, http.StatusSeeOther)
			return
		}
		core.WriteJSON(w, http.StatusCreated, view(db))
	}
	mux.HandleFunc("POST /api/v1/dashboard/databases", createDatabase)
	mux.HandleFunc("POST /api/v1/dashboard/databases/create", createDatabase)
	deleteDatabase := func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		p, _, project, err := selected(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		if !deps.Store.Can(p, core.Organization.CanManage) {
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
			project.RemoveDatabase(db.ID, time.Now().UTC())
			org := a.Organizations[p.OrganizationID]
			org.Projects[p.ProjectID] = project
			a.Organizations[p.OrganizationID] = org
			return nil
		})
		if err != nil {
			writeError(w, err)
			return
		}
		if core.WantsHTML(r) {
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
	Region       string
	Error        string
	Search       string
	OpenCreate   bool
	CanProvision bool
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

func renderList(w http.ResponseWriter, r *http.Request, deps core.Deps) {
	p, a, project, err := selected(deps, r)
	if err != nil {
		if errors.Is(err, core.ErrUnauthorized) {
			core.RedirectToLogin(w, r)
			return
		}
		writeError(w, err)
		return
	}
	data := pageData{ProjectID: project.ID, Summary: summarize(project.Databases), Region: cmp.Or(project.Region, "GRU"), Error: r.URL.Query().Get("error"), Search: strings.TrimSpace(r.URL.Query().Get("q")), OpenCreate: r.URL.Query().Get("create") == "1"}
	if len(data.Search) > 256 {
		data.Search = data.Search[:256]
	}
	data.CanProvision = a.Organizations[p.OrganizationID].CanProvisionResources(p.UserID)
	search := strings.ToLower(data.Search)
	for _, db := range project.Databases {
		if data.Search == "" || strings.Contains(strings.ToLower(db.Name), search) {
			data.Databases = append(data.Databases, view(db))
		}
	}
	slices.SortFunc(data.Databases, func(a, b databaseView) int { return strings.Compare(a.Name, b.Name) })
	page := core.Page{Layout: "shell", Title: "Databases", Principal: p, View: func(page core.Page) templ.Component { return databasesContent(page, data) }, CSRFToken: deps.CSRF.Token(w, r)}
	if err := deps.Render.Page(w, r, page); err != nil {
		http.Error(w, "could not render databases", http.StatusInternalServerError)
	}
}

func selected(deps core.Deps, r *http.Request) (core.Principal, core.Account, core.Project, error) {
	p, ok := deps.Sessions.Principal(r)
	if !ok || p.ProjectID == "" {
		return core.Principal{}, core.Account{}, core.Project{}, core.ErrUnauthorized
	}
	a, err := deps.Store.SnapshotAs(p)
	if err != nil {
		return core.Principal{}, core.Account{}, core.Project{}, err
	}
	project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
	return p, a, project, err
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

func writeError(w http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, core.ErrDatabaseLimit):
		http.Error(w, core.ErrDatabaseLimit.Error(), http.StatusConflict)
	case core.WriteAccessError(w, err):
	case errors.Is(err, core.ErrConflict):
		http.Error(w, "conflict", http.StatusConflict)
	default:
		http.Error(w, "invalid request", http.StatusBadRequest)
	}
}
