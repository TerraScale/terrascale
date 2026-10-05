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
	Placement []string  `json:"placement"`
	CreatedAt time.Time `json:"createdAt"`
}

func view(db core.Database) databaseView {
	return databaseView{ID: db.ID, Name: db.Name, Status: db.Status, Region: db.Region, Placement: slices.Clone(databaseRegions(db)), CreatedAt: db.CreatedAt}
}

func databaseRegions(db core.Database) []string {
	if len(db.Regions) > 0 {
		return db.Regions
	}
	if db.Region != "" {
		return []string{db.Region}
	}
	return nil
}

// Mount owns the database lifecycle routes and tools for the selected project.
func Mount(mux *http.ServeMux, deps core.Deps) {
	service := Service{deps: deps}
	mountTools(deps.Tools, service)
	mux.HandleFunc("GET /databases", func(w http.ResponseWriter, r *http.Request) { renderList(w, r, deps) })
	mountDetail(mux, deps)
	mux.HandleFunc("GET /api/v1/dashboard/databases", func(w http.ResponseWriter, r *http.Request) {
		p, ok := deps.Sessions.Principal(r)
		if !ok {
			writeError(w, core.ErrUnauthorized)
			return
		}
		list, err := service.List(r.Context(), p)
		if err != nil {
			writeError(w, err)
			return
		}
		core.WriteJSON(w, http.StatusOK, list)
	})
	mux.HandleFunc("POST /api/v1/dashboard/databases", func(w http.ResponseWriter, r *http.Request) {
		p, ok := deps.FormPrincipal(w, r)
		if !ok {
			return
		}
		name, regions := strings.TrimSpace(r.FormValue("name")), r.Form["region"]
		if len(regions) == 0 && core.WantsHTML(r) {
			FormFailed(w, r, http.StatusBadRequest, "/databases", ErrInvalidRegions, url.Values{"create": {"1"}, "name": {name}, "regionSelection": {"1"}})
			return
		}
		db, err := service.Create(r.Context(), p, name, regions...)
		if err != nil {
			// Reopen the create dialog with the error and the input.
			invalid := ErrInvalidRegions
			if errors.Is(err, errInvalidName) {
				invalid = ErrInvalidName
			}
			status, code := failure(err, invalid)
			FormFailed(w, r, status, "/databases", code, url.Values{"create": {"1"}, "name": {name}, "region": regions, "regionSelection": {"1"}})
			return
		}
		if core.WantsHTML(r) {
			http.Redirect(w, r, "/databases/"+url.PathEscape(db.ID), http.StatusSeeOther)
			return
		}
		core.WriteJSON(w, http.StatusCreated, db)
	})
	mux.HandleFunc("POST /api/v1/dashboard/databases/branch", func(w http.ResponseWriter, r *http.Request) {
		p, ok := deps.FormPrincipal(w, r)
		if !ok {
			return
		}
		sourceID := r.FormValue("databaseId")
		branch, err := service.Branch(r.Context(), p, sourceID, r.FormValue("name"))
		if errors.Is(err, core.ErrNotFound) {
			// The source database, and so its branches tab, is gone.
			writeError(w, err)
			return
		}
		if err != nil {
			status, code := failure(err, ErrInvalidName)
			FormFailed(w, r, status, TabPath(sourceID, "branches", nil), code, nil)
			return
		}
		if core.WantsHTML(r) {
			http.Redirect(w, r, "/databases/"+url.PathEscape(branch.ID), http.StatusSeeOther)
			return
		}
		core.WriteJSON(w, http.StatusCreated, branch)
	})
	mux.HandleFunc("POST /api/v1/dashboard/databases/delete", func(w http.ResponseWriter, r *http.Request) {
		p, ok := deps.FormPrincipal(w, r)
		if !ok {
			return
		}
		databaseID := r.FormValue("databaseId")
		confirmation := r.FormValue("confirmName")
		if _, hasConfirmName := r.Form["confirmName"]; !hasConfirmName {
			confirmation = strings.TrimSpace(r.FormValue("confirm"))
		}
		err := service.Delete(r.Context(), p, databaseID, confirmation)
		if errors.Is(err, core.ErrNotFound) {
			writeError(w, err)
			return
		}
		if err != nil {
			// Reopen the delete dialog with the error.
			status, code := failure(err, ErrConfirmName)
			FormFailed(w, r, status, TabPath(databaseID, "overview", nil), code, url.Values{"delete": {"1"}})
			return
		}
		if core.WantsHTML(r) {
			http.Redirect(w, r, "/databases", http.StatusSeeOther)
			return
		}
		w.WriteHeader(http.StatusNoContent)
	})
}

type pageData struct {
	ProjectID    string
	Summary      databaseSummary
	Databases    []databaseView
	Regions      []string
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
		for _, region := range databaseRegions(db) {
			if !slices.Contains(summary.Regions, region) {
				summary.Regions = append(summary.Regions, region)
			}
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
		Regions:    []string{cmp.Or(project.Region, "GRU")},
		Error:      formError(query.Get("error")),
		Search:     strings.TrimSpace(query.Get("q")),
		OpenCreate: query.Get("create") == "1",
		Name:       query.Get("name"),
	}
	if query.Get("regionSelection") == "1" {
		data.Regions = nil
		for _, value := range query["region"] {
			if region, err := sdk.ParseRegionCode(value); err == nil && !slices.Contains(data.Regions, string(region)) {
				data.Regions = append(data.Regions, string(region))
			}
		}
	}
	if len(data.Search) > 256 {
		data.Search = data.Search[:256]
	}
	data.Name = truncate(data.Name, 63)
	org := a.Organizations[p.OrganizationID]
	data.CanProvision = org.CanProvisionResources(p.UserID)
	plan, err := deps.Store.Plan(r.Context(), org.PlanID)
	if err != nil && !errors.Is(err, core.ErrNotFound) {
		if !core.WriteAccessError(w, err) {
			http.Error(w, "could not render databases", http.StatusInternalServerError)
		}
		return
	}
	if err == nil && plan.MaxDatabases > 0 && org.DatabaseCount() >= plan.MaxDatabases {
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
	a, err := deps.Store.SnapshotAs(r.Context(), p)
	if err != nil {
		return core.Principal{}, core.Account{}, core.Project{}, err
	}
	project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
	return p, a, project, err
}

// failure maps an error from a database change to its status and error
// code. invalid is the code for the form's own input being rejected.
func failure(err error, invalid string) (int, string) {
	var provider core.ProviderError
	switch {
	case errors.Is(err, core.ErrInvalid):
		return http.StatusBadRequest, invalid
	case errors.Is(err, errNotReady):
		return http.StatusConflict, ErrNotReady
	case errors.Is(err, errProvisioning):
		return http.StatusConflict, ErrProvisioning
	case errors.Is(err, core.ErrDatabaseLimit):
		return http.StatusConflict, ErrPlanLimit
	case errors.Is(err, core.ErrConflict):
		return http.StatusConflict, ErrNameTaken
	case errors.Is(err, core.ErrForbidden), errors.Is(err, core.ErrUnauthorized):
		return http.StatusForbidden, ErrForbidden
	case errors.Is(err, core.ErrNotFound):
		return http.StatusNotFound, ErrMissing
	case errors.As(err, &provider):
		return http.StatusBadGateway, ErrProvider
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
