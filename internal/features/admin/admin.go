// Package admin owns platform administrator inventories and management actions.
package admin

import (
	"errors"
	"net/http"
	"net/url"
	"slices"
	"strconv"
	"strings"
	"time"
	"unicode/utf8"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/notfound"
)

type userRow struct {
	AccountID         string `json:"accountId"`
	UserID            string `json:"userId"`
	Name              string `json:"name"`
	Email             string `json:"email"`
	PlatformAdmin     bool   `json:"platformAdmin"`
	CreatedAt         string `json:"createdAt"`
	OrganizationCount int    `json:"organizationCount"`
	ProjectCount      int    `json:"projectCount"`
}

type organizationRow struct {
	AccountID    string `json:"accountId"`
	ID           string `json:"id"`
	Name         string `json:"name"`
	Status       string `json:"status"`
	OwnerEmail   string `json:"ownerEmail"`
	ProjectCount int    `json:"projectCount"`
	CreatedAt    string `json:"createdAt"`
}

type projectRow struct {
	AccountID      string `json:"accountId"`
	OrganizationID string `json:"organizationId"`
	Organization   string `json:"organization"`
	ID             string `json:"id"`
	Name           string `json:"name"`
	Region         string `json:"region"`
	Status         string `json:"status"`
	DatabaseCount  int    `json:"databaseCount"`
	CreatedAt      string `json:"createdAt"`
}

type databaseRow struct {
	AccountID      string `json:"accountId"`
	OrganizationID string `json:"organizationId"`
	Organization   string `json:"organization"`
	ProjectID      string `json:"projectId"`
	Project        string `json:"project"`
	ID             string `json:"id"`
	Name           string `json:"name"`
	Region         string `json:"region"`
	Status         string `json:"status"`
	CreatedAt      string `json:"createdAt"`
}

type estateRow struct {
	Region            string `json:"region"`
	Location          string `json:"location"`
	ObservedProjects  int    `json:"observedProjects"`
	ObservedDatabases int    `json:"observedDatabases"`
}

type inventoryCounts struct {
	Accounts      int `json:"accounts"`
	Organizations int `json:"organizations"`
	Projects      int `json:"projects"`
	Databases     int `json:"databases"`
}

type pageData struct {
	Section             string
	Title               string
	Query               string
	Notice              string
	Error               string
	Users               []userRow
	Organizations       []organizationRow
	Projects            []projectRow
	Databases           []databaseRow
	Regions             []estateRow
	EstateTab           string
	EstateDatabases     []databaseRow
	EstateActions       []core.AdminAction
	EstateRegionCount   int
	EstateDatabaseCount int
	EstateActionCount   int
	Plans               []core.Plan
	Inventory           inventoryCounts
	Demo                bool
}

var adminEstateTabs = [...]string{"regions", "hosts", "operations"}

// Mount registers the in-memory platform administration pages and actions.
func Mount(mux *http.ServeMux, deps core.Deps) {
	service := NewService(deps.Store)
	mountTools(deps.Tools, service)
	mux.HandleFunc("GET /admin", adminRoot(deps))
	mux.HandleFunc("GET /admin/users", adminPage(deps, service, "users"))
	mux.HandleFunc("GET /admin/organizations", adminPage(deps, service, "organizations"))
	mux.HandleFunc("GET /admin/projects", adminPage(deps, service, "projects"))
	mux.HandleFunc("GET /admin/databases", adminPage(deps, service, "databases"))
	mux.HandleFunc("GET /admin/estate", adminPage(deps, service, "estate"))
	mux.HandleFunc("GET /admin/usage", adminPage(deps, service, "usage"))
	mux.HandleFunc("GET /admin/plans", adminPage(deps, service, "plans"))
	mux.HandleFunc("GET /admin/pricing", func(w http.ResponseWriter, r *http.Request) {
		if _, ok := requireAdminRoute(deps, w, r); !ok {
			return
		}
		http.Redirect(w, r, "/admin/plans", http.StatusFound)
	})
	mux.HandleFunc("GET /admin/", func(w http.ResponseWriter, r *http.Request) {
		if _, ok := requireAdminRoute(deps, w, r); !ok {
			return
		}
		notfound.Render(w, r)
	})

	mux.HandleFunc("POST /api/v1/admin/users/{accountID}", updateUser(deps, service))
	mux.HandleFunc("POST /api/v1/admin/organizations/{accountID}/{organizationID}", renameOrganization(deps, service))
	mux.HandleFunc("POST /api/v1/admin/projects/{accountID}/{organizationID}/{projectID}", renameProject(deps, service))
	mux.HandleFunc("POST /api/v1/admin/estate/action", recordEstateAction(deps, service))
	mux.HandleFunc("POST /api/v1/admin/plans", savePlan(deps, service))
	mux.HandleFunc("POST /api/v1/admin/plans/{planID}/visibility", setPlanVisibility(deps, service))
	mux.HandleFunc("POST /api/v1/admin/plans/{planID}/delete", deletePlan(deps, service))
}

func adminRoot(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if _, ok := requireAdminRoute(deps, w, r); !ok {
			return
		}
		http.Redirect(w, r, "/admin/users", http.StatusFound)
	}
}

func adminPage(deps core.Deps, service Service, section string) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		principal, ok := requireAdminRoute(deps, w, r)
		if !ok {
			return
		}
		data, err := service.page(r.Context(), principal, section, r.URL.Query().Get("q"))
		if err != nil {
			writeAdminError(w, err)
			return
		}
		if section == "estate" {
			data.EstateTab = adminEstateTab(r.URL.Query().Get("tab"))
		}
		data.Notice = r.URL.Query().Get("notice")
		data.Error = r.URL.Query().Get("error")
		data.Demo = deps.Demo
		page := core.Page{
			Layout: "admin", Title: data.Title,
			Principal: principal,
			View: func(p core.Page) templ.Component {
				return adminView(data, p.CSRFToken)
			},
		}
		if deps.CSRF != nil {
			page.CSRFToken = deps.CSRF.Token(w, r)
		}
		if err := deps.Render.Page(w, r, page); err != nil {
			http.Error(w, "could not render admin page", http.StatusInternalServerError)
		}
	}
}

func adminEstateTab(tab string) string {
	if slices.Contains(adminEstateTabs[:], tab) {
		return tab
	}
	return "regions"
}

func updateUser(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		principal, ok := authorizeAdminPost(deps, w, r)
		if !ok {
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid admin form", http.StatusBadRequest)
			return
		}
		updatedAdmin := r.FormValue("platformAdmin") == "true"
		if err := service.UpdateUser(r.Context(), principal, r.PathValue("accountID"), r.FormValue("name"), updatedAdmin); err != nil {
			writeMutationError(w, r, "/admin/users", err)
			return
		}
		if principal.AccountID == r.PathValue("accountID") && !updatedAdmin {
			http.Redirect(w, r, "/", http.StatusSeeOther)
			return
		}
		redirectNotice(w, r, "/admin/users", "User updated.")
	}
}

func renameOrganization(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		principal, ok := authorizeAdminPost(deps, w, r)
		if !ok {
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid admin form", http.StatusBadRequest)
			return
		}
		if err := service.RenameOrganization(r.Context(), principal, r.PathValue("accountID"), r.PathValue("organizationID"), r.FormValue("name")); err != nil {
			writeMutationError(w, r, "/admin/organizations", err)
			return
		}
		redirectNotice(w, r, "/admin/organizations", "Organization updated.")
	}
}

func renameProject(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		principal, ok := authorizeAdminPost(deps, w, r)
		if !ok {
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid admin form", http.StatusBadRequest)
			return
		}
		if err := service.RenameProject(r.Context(), principal, r.PathValue("accountID"), r.PathValue("organizationID"), r.PathValue("projectID"), r.FormValue("name")); err != nil {
			writeMutationError(w, r, "/admin/projects", err)
			return
		}
		redirectNotice(w, r, "/admin/projects", "Project updated.")
	}
}

func recordEstateAction(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		principal, ok := authorizeAdminPost(deps, w, r)
		if !ok {
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid admin form", http.StatusBadRequest)
			return
		}
		if err := service.RecordEstateAction(
			r.Context(),
			principal,
			r.FormValue("action"),
			r.FormValue("targetId"),
			r.FormValue("reason"),
		); err != nil {
			if errors.Is(err, core.ErrInvalid) {
				redirectEstateAction(w, r, "error", "Choose an allowed action, enter a target, and provide a reason between 3 and 1000 characters.")
				return
			}
			writeMutationError(w, r, "/admin/estate", err)
			return
		}
		redirectEstateAction(w, r, "notice", "Action completed and recorded in the audit log. No infrastructure action was taken.")
	}
}

func savePlan(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		principal, ok := authorizeAdminPost(deps, w, r)
		if !ok {
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid admin form", http.StatusBadRequest)
			return
		}
		price, priceOK := nonNegativeInt64(r.FormValue("priceCents"))
		annualPrice, annualOK := nonNegativeInt64(r.FormValue("annualPriceCents"))
		trialDays, trialOK := nonNegativeInt(r.FormValue("trialDays"))
		storage, storageOK := nonNegativeInt64(r.FormValue("storageMiB"))
		requests, requestsOK := nonNegativeInt64(r.FormValue("monthlyRequests"))
		if !priceOK || !annualOK || !trialOK || !storageOK || !requestsOK {
			redirectError(w, r, "/admin/plans", "Enter non-negative whole numbers for plan limits and prices.")
			return
		}
		plan := core.Plan{
			ID: r.FormValue("planID"), Key: r.FormValue("key"), Name: r.FormValue("name"),
			PriceCents: price, AnnualPriceCents: annualPrice, TrialDays: trialDays,
			Visible: r.FormValue("visible") == "true", StorageMiB: storage, MonthlyRequests: requests,
		}
		if _, err := service.SavePlan(r.Context(), principal, plan); err != nil {
			writeMutationError(w, r, "/admin/plans", err)
			return
		}
		redirectNotice(w, r, "/admin/plans", "Pricing plan saved.")
	}
}

func setPlanVisibility(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		principal, ok := authorizeAdminPost(deps, w, r)
		if !ok {
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid admin form", http.StatusBadRequest)
			return
		}
		if err := service.SetPlanVisibility(r.Context(), principal, r.PathValue("planID"), r.FormValue("visible") == "true"); err != nil {
			writeMutationError(w, r, "/admin/plans", err)
			return
		}
		redirectNotice(w, r, "/admin/plans", "Pricing plan visibility updated.")
	}
}

func deletePlan(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		principal, ok := authorizeAdminPost(deps, w, r)
		if !ok {
			return
		}
		if err := service.DeletePlan(r.Context(), principal, r.PathValue("planID")); err != nil {
			writeMutationError(w, r, "/admin/plans", err)
			return
		}
		redirectNotice(w, r, "/admin/plans", "Pricing plan deleted.")
	}
}

func requireAdminRoute(deps core.Deps, w http.ResponseWriter, r *http.Request) (core.Principal, bool) {
	if deps.Sessions == nil || deps.Store == nil {
		http.Error(w, "authentication unavailable", http.StatusUnauthorized)
		return core.Principal{}, false
	}
	principal, ok := deps.Sessions.Principal(r)
	if !ok {
		core.RedirectToLogin(w, r)
		return core.Principal{}, false
	}
	if err := deps.Store.AuthorizePlatformAdmin(r.Context(), principal); err != nil {
		writeAdminError(w, err)
		return core.Principal{}, false
	}
	return principal, true
}

func authorizeAdminPost(deps core.Deps, w http.ResponseWriter, r *http.Request) (core.Principal, bool) {
	principal, ok := requireAdminRoute(deps, w, r)
	if !ok {
		return core.Principal{}, false
	}
	if !deps.VerifyCSRF(w, r) {
		return core.Principal{}, false
	}
	return principal, true
}

func writeAdminError(w http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, core.ErrForbidden), errors.Is(err, core.ErrUnauthorized):
		http.Error(w, "forbidden", http.StatusForbidden)
	case errors.Is(err, core.ErrNotFound):
		http.Error(w, "not found", http.StatusNotFound)
	case errors.Is(err, core.ErrInvalid):
		http.Error(w, "invalid admin request", http.StatusBadRequest)
	case errors.Is(err, core.ErrUnavailable):
		http.Error(w, "service unavailable", http.StatusServiceUnavailable)
	default:
		http.Error(w, "could not load admin data", http.StatusInternalServerError)
	}
}

func writeMutationError(w http.ResponseWriter, r *http.Request, path string, err error) {
	switch {
	case errors.Is(err, core.ErrForbidden), errors.Is(err, core.ErrUnauthorized):
		http.Error(w, "forbidden", http.StatusForbidden)
	case errors.Is(err, core.ErrNotFound):
		redirectError(w, r, path, "That record is no longer available.")
	case errors.Is(err, core.ErrConflict):
		redirectError(w, r, path, "That change conflicts with existing data.")
	case errors.Is(err, core.ErrInvalid):
		redirectError(w, r, path, "Enter valid values for the selected fields.")
	case errors.Is(err, core.ErrUnavailable):
		http.Error(w, "service unavailable", http.StatusServiceUnavailable)
	default:
		http.Error(w, "could not update admin data", http.StatusInternalServerError)
	}
}

func redirectNotice(w http.ResponseWriter, r *http.Request, path, notice string) {
	http.Redirect(w, r, path+"?notice="+url.QueryEscape(notice), http.StatusSeeOther)
}

func redirectError(w http.ResponseWriter, r *http.Request, path, message string) {
	http.Redirect(w, r, path+"?error="+url.QueryEscape(message), http.StatusSeeOther)
}

func redirectEstateAction(w http.ResponseWriter, r *http.Request, key, message string) {
	query := url.Values{"tab": {"operations"}, key: {message}}
	http.Redirect(w, r, "/admin/estate?"+query.Encode(), http.StatusSeeOther)
}

func nonNegativeInt64(value string) (int64, bool) {
	parsed, err := strconv.ParseInt(value, 10, 64)
	return parsed, err == nil && parsed >= 0
}

func nonNegativeInt(value string) (int, bool) {
	parsed, err := strconv.Atoi(value)
	return parsed, err == nil && parsed >= 0
}

func formatTime(value time.Time) string {
	if value.IsZero() {
		return "-"
	}
	return value.UTC().Format("2006-01-02 15:04 UTC")
}

func truncateQuery(query string) string {
	query = strings.TrimSpace(query)
	if utf8.RuneCountInString(query) <= 200 {
		return query
	}
	runes := []rune(query)
	return string(runes[:200])
}

func matching(query string, values ...string) bool {
	if query == "" {
		return true
	}
	query = strings.ToLower(query)
	for _, value := range values {
		if strings.Contains(strings.ToLower(value), query) {
			return true
		}
	}
	return false
}

func sortRows(data *pageData) {
	slices.SortFunc(data.Users, func(a, b userRow) int { return strings.Compare(strings.ToLower(a.Email), strings.ToLower(b.Email)) })
	slices.SortFunc(data.Organizations, func(a, b organizationRow) int {
		return strings.Compare(strings.ToLower(a.Name), strings.ToLower(b.Name))
	})
	slices.SortFunc(data.Projects, func(a, b projectRow) int { return strings.Compare(strings.ToLower(a.Name), strings.ToLower(b.Name)) })
	slices.SortFunc(data.Databases, func(a, b databaseRow) int { return strings.Compare(strings.ToLower(a.Name), strings.ToLower(b.Name)) })
	slices.SortFunc(data.EstateDatabases, func(a, b databaseRow) int { return strings.Compare(strings.ToLower(a.Name), strings.ToLower(b.Name)) })
	slices.SortFunc(data.Regions, func(a, b estateRow) int { return strings.Compare(strings.ToLower(a.Region), strings.ToLower(b.Region)) })
	slices.SortFunc(data.Plans, func(a, b core.Plan) int { return strings.Compare(strings.ToLower(a.Name), strings.ToLower(b.Name)) })
}
