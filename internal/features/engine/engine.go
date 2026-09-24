// Package engine owns the contract-backed TerraBase health and region view.
package engine

import (
	"embed"
	"errors"
	"io/fs"
	"net/http"
	"slices"
	"strconv"
	"strings"

	"terrascale.tech/dashboard/internal/core"
)

const pageSize = 10

//go:embed templates/*.html
var featureTemplates embed.FS

type healthView struct {
	Region  string
	Version string
	Healthy bool
}

type regionView struct {
	Code      string
	Name      string
	Country   string
	Latitude  float64
	Longitude float64
	Available bool
}

type pageData struct {
	Configured    bool
	ConfigMessage string
	Health        *healthView
	HealthError   string
	Regions       []regionView
	RegionCount   int
	HostCount     int
	CatalogError  string
	Page          int
	PageCount     int
	Previous      int
	Next          int
	HasPrevious   bool
	HasNext       bool
}

// Mount registers the authenticated engine overview.
func Mount(mux *http.ServeMux, deps core.Deps) {
	templates, err := fs.Sub(featureTemplates, "templates")
	if err != nil {
		panic(err)
	}
	if err := deps.Render.Register("engine", templates, "*.html"); err != nil {
		panic(err)
	}

	mux.HandleFunc("GET /engine", func(w http.ResponseWriter, r *http.Request) {
		p, ok := principal(deps, w, r)
		if !ok {
			return
		}
		if _, err := deps.Store.SnapshotAs(p); err != nil {
			writeError(w, err)
			return
		}
		data := load(r, deps, p)
		page := core.Page{Template: "engine", Layout: "shell", Title: "Engine", Principal: p, Data: data}
		if deps.CSRF != nil {
			page.CSRFToken = deps.CSRF.Token(w, r)
		}
		if err := deps.Render.Page(w, r, page); err != nil {
			http.Error(w, "could not render engine status", http.StatusInternalServerError)
		}
	})
}

func load(r *http.Request, deps core.Deps, p core.Principal) pageData {
	data := pageData{Page: 1, PageCount: 1, Previous: 1, Next: 1}
	if deps.SDK == nil {
		data.ConfigMessage = "The TerraBase provider is not configured for this dashboard."
		return data
	}
	client, err := deps.SDK.ForScope(p.OrganizationID, p.ProjectID)
	if err != nil {
		data.ConfigMessage = "The TerraBase provider is not configured for this project."
		return data
	}
	data.Configured = true

	health, err := client.Regions.Health(r.Context())
	if err != nil {
		data.HealthError = "Member health could not be loaded from the configured TerraBase provider."
	} else {
		data.Health = &healthView{Region: string(health.Region), Version: health.Version, Healthy: health.Healthy}
	}

	catalog, err := client.Regions.List(r.Context())
	if err != nil {
		data.CatalogError = "The provider region catalog could not be loaded."
		return data
	}
	all := make([]regionView, 0, len(catalog.Regions))
	for _, region := range catalog.Regions {
		all = append(all, regionView{
			Code: string(region.Code), Name: region.Name, Country: region.Country,
			Latitude: region.Latitude, Longitude: region.Longitude, Available: region.Available,
		})
	}
	slices.SortFunc(all, func(a, b regionView) int { return strings.Compare(a.Code, b.Code) })
	data.RegionCount = len(all)
	data.HostCount = len(catalog.Hosts)
	data.PageCount = max(1, (len(all)+pageSize-1)/pageSize)
	data.Page = clampPage(r.URL.Query().Get("page"), data.PageCount)
	start := (data.Page - 1) * pageSize
	data.Regions = all[start:min(start+pageSize, len(all))]
	data.Previous = max(1, data.Page-1)
	data.Next = min(data.PageCount, data.Page+1)
	data.HasPrevious = data.Page > 1
	data.HasNext = data.Page < data.PageCount
	return data
}

func principal(deps core.Deps, w http.ResponseWriter, r *http.Request) (core.Principal, bool) {
	if deps.Sessions == nil {
		http.Error(w, "authentication required", http.StatusUnauthorized)
		return core.Principal{}, false
	}
	p, ok := deps.Sessions.Principal(r)
	if !ok {
		http.Redirect(w, r, "/login", http.StatusSeeOther)
		return core.Principal{}, false
	}
	return p, true
}

func clampPage(raw string, pages int) int {
	page, err := strconv.Atoi(raw)
	if err != nil || page < 1 {
		return 1
	}
	return min(page, pages)
}

func writeError(w http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, core.ErrUnauthorized):
		http.Error(w, "authentication required", http.StatusUnauthorized)
	case errors.Is(err, core.ErrForbidden):
		http.Error(w, "forbidden", http.StatusForbidden)
	case errors.Is(err, core.ErrNotFound):
		http.Error(w, "not found", http.StatusNotFound)
	default:
		http.Error(w, "invalid request", http.StatusBadRequest)
	}
}
