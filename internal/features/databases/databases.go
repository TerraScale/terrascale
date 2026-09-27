package databases

import (
	"cmp"
	"errors"
	"net/http"
	"net/url"
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
		// failed reopens the create dialog with the error and the input.
		failed := func(status int, code string) {
			FormFailed(w, r, status, "/databases", code, url.Values{"create": {"1"}, "name": {name}, "region": {regionValue}})
		}
		region, err := sdk.ParseRegionCode(regionValue)
		if !core.ValidDatabaseName(name) || err != nil {
			failed(http.StatusBadRequest, ErrInvalidName)
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
			failed(failure(err))
			return
		}
		if !deps.Demo {
			if deps.SDK == nil {
				setStatus(deps, p, db.ID, "failed")
				failed(http.StatusServiceUnavailable, ErrUnavailable)
				return
			}
			client, err := deps.SDK.ForScope(p.OrganizationID, p.ProjectID)
			if err != nil {
				setStatus(deps, p, db.ID, "failed")
				failed(http.StatusServiceUnavailable, ErrUnavailable)
				return
			}
			operationID := db.ID
			result, err := client.Databases.CreateDatabase(r.Context(), sdk.CreateDatabaseRequest{Name: name, Placement: []sdk.RegionCode{region}, OperationID: operationID}, sdk.WithIdempotencyKey(operationID))
			if err != nil {
				setStatus(deps, p, db.ID, "failed")
				failed(http.StatusBadGateway, ErrProvider)
				return
			}
			if result.Operation != nil {
				db.Status = result.Operation.State
			} else if result.Database != nil {
				db.Status = "ready"
			} else {
				setStatus(deps, p, db.ID, "failed")
				failed(http.StatusBadGateway, ErrProvider)
				return
			}
			if err := setStatus(deps, p, db.ID, db.Status); err != nil {
				failed(failure(err))
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
		// failed reopens the delete dialog with the error.
		failed := func(status int, code string) {
			FormFailed(w, r, status, TabPath(db.ID, "overview", nil), code, url.Values{"delete": {"1"}})
		}
		if r.Method == http.MethodPost {
			confirmation := r.FormValue("confirmName")
			if _, hasConfirmName := r.Form["confirmName"]; !hasConfirmName {
				confirmation = strings.TrimSpace(r.FormValue("confirm"))
			}
			if confirmation != db.Name {
				failed(http.StatusBadRequest, ErrConfirmName)
				return
			}
		}
		if provisioning(db.Status) {
			failed(http.StatusConflict, ErrProvisioning)
			return
		}
		if !deps.Demo {
			if deps.SDK == nil {
				failed(http.StatusServiceUnavailable, ErrUnavailable)
				return
			}
			client, err := deps.SDK.ForScope(p.OrganizationID, p.ProjectID)
			if err != nil {
				failed(http.StatusServiceUnavailable, ErrUnavailable)
				return
			}
			opID := core.NewID("del")
			if _, err := client.Databases.DeleteDatabase(r.Context(), db.Name, sdk.WithIdempotencyKey(opID), sdk.WithDeleteConfirmation(db.Name)); err != nil {
				failed(http.StatusBadGateway, ErrProvider)
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
			failed(failure(err))
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
	// Name refills the create dialog after a refused post.
	Name string
	// Limit explains a plan whose database allowance is used up.
	Limit *planLimit
}

// planLimit is a plan's database allowance when every database is in use.
type planLimit struct {
	Plan      string
	Databases int
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

// provisioning reports whether a database is still being created.
func provisioning(status string) bool {
	return status == "provisioning" || status == "accepted" || status == "pending"
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
	query := r.URL.Query()
	data := pageData{
		ProjectID:  project.ID,
		Summary:    summarize(project.Databases),
		Region:     cmp.Or(project.Region, "GRU"),
		Error:      formError(query.Get("error")),
		Search:     strings.TrimSpace(query.Get("q")),
		OpenCreate: query.Get("create") == "1",
		Name:       query.Get("name"),
	}
	if region, err := sdk.ParseRegionCode(query.Get("region")); err == nil {
		data.Region = string(region)
	}
	if len(data.Search) > 256 {
		data.Search = data.Search[:256]
	}
	data.Name = truncate(data.Name, 63)
	org := a.Organizations[p.OrganizationID]
	data.CanProvision = org.CanProvisionResources(p.UserID)
	if plan, ok := deps.Store.Plan(org.PlanID); ok && plan.MaxDatabases > 0 && org.DatabaseCount() >= plan.MaxDatabases {
		data.Limit = &planLimit{Plan: plan.Name, Databases: plan.MaxDatabases}
	}
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

// failure maps an error from a database change to its status and error code.
func failure(err error) (int, string) {
	switch {
	case errors.Is(err, core.ErrDatabaseLimit):
		return http.StatusConflict, ErrPlanLimit
	case errors.Is(err, core.ErrConflict):
		return http.StatusConflict, ErrNameTaken
	case errors.Is(err, core.ErrForbidden), errors.Is(err, core.ErrUnauthorized):
		return http.StatusForbidden, ErrForbidden
	case errors.Is(err, core.ErrNotFound):
		return http.StatusNotFound, ErrMissing
	default:
		return http.StatusServiceUnavailable, ErrUnavailable
	}
}

func writeError(w http.ResponseWriter, err error) {
	switch {
	case core.WriteAccessError(w, err):
	case errors.Is(err, core.ErrConflict):
		http.Error(w, "conflict", http.StatusConflict)
	default:
		http.Error(w, "invalid request", http.StatusBadRequest)
	}
}
