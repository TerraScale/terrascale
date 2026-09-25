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
	AccountID         string
	UserID            string
	Name              string
	Email             string
	PlatformAdmin     bool
	CreatedAt         string
	OrganizationCount int
	ProjectCount      int
}

type organizationRow struct {
	AccountID    string
	ID           string
	Name         string
	Status       string
	OwnerEmail   string
	ProjectCount int
	CreatedAt    string
}

type projectRow struct {
	AccountID      string
	OrganizationID string
	Organization   string
	ID             string
	Name           string
	Region         string
	Status         string
	DatabaseCount  int
	CreatedAt      string
}

type databaseRow struct {
	AccountID      string
	OrganizationID string
	Organization   string
	ProjectID      string
	Project        string
	ID             string
	Name           string
	Region         string
	Status         string
	CreatedAt      string
}

type estateRow struct {
	Region            string
	ObservedProjects  int
	ObservedDatabases int
}

type inventoryCounts struct {
	Accounts      int
	Organizations int
	Projects      int
	Databases     int
}

type pageData struct {
	Section       string
	Title         string
	Query         string
	Notice        string
	Error         string
	Users         []userRow
	Organizations []organizationRow
	Projects      []projectRow
	Databases     []databaseRow
	Regions       []estateRow
	EstateActions []core.AdminAction
	Plans         []core.Plan
	Inventory     inventoryCounts
}

// Mount registers the in-memory platform administration pages and actions.
func Mount(mux *http.ServeMux, deps core.Deps) {
	service := NewService(deps.Store)
	mux.HandleFunc("GET /admin", adminRoot(deps, service))
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

func adminRoot(deps core.Deps, service Service) http.HandlerFunc {
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
		data, err := service.page(principal, section, r.URL.Query().Get("q"))
		if err != nil {
			writeAdminError(w, err)
			return
		}
		data.Notice = r.URL.Query().Get("notice")
		data.Error = r.URL.Query().Get("error")
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
		if err := service.UpdateUser(principal, r.PathValue("accountID"), r.FormValue("name"), updatedAdmin); err != nil {
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
		if err := service.RenameOrganization(principal, r.PathValue("accountID"), r.PathValue("organizationID"), r.FormValue("name")); err != nil {
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
		if err := service.RenameProject(principal, r.PathValue("accountID"), r.PathValue("organizationID"), r.PathValue("projectID"), r.FormValue("name")); err != nil {
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
			principal,
			r.FormValue("action"),
			r.FormValue("targetId"),
			r.FormValue("reason"),
		); err != nil {
			if errors.Is(err, core.ErrInvalid) {
				redirectError(w, r, "/admin/estate", "Choose an allowed action, enter a target, and provide a reason between 3 and 1000 characters.")
				return
			}
			writeMutationError(w, r, "/admin/estate", err)
			return
		}
		redirectNotice(w, r, "/admin/estate", "Action completed and recorded in the audit log. No infrastructure action was taken.")
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
		if err := service.SavePlan(principal, plan); err != nil {
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
		if err := service.SetPlanVisibility(principal, r.PathValue("planID"), r.FormValue("visible") == "true"); err != nil {
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
		if err := service.DeletePlan(principal, r.PathValue("planID")); err != nil {
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
		http.Redirect(w, r, "/login", http.StatusSeeOther)
		return core.Principal{}, false
	}
	if _, err := deps.Store.SnapshotAs(principal); err != nil {
		writeAdminError(w, err)
		return core.Principal{}, false
	}
	if _, err := deps.Store.PlatformAccounts(principal); err != nil {
		if errors.Is(err, core.ErrForbidden) {
			http.Error(w, "forbidden", http.StatusForbidden)
		} else {
			writeAdminError(w, err)
		}
		return core.Principal{}, false
	}
	return principal, true
}

func authorizeAdminPost(deps core.Deps, w http.ResponseWriter, r *http.Request) (core.Principal, bool) {
	principal, ok := requireAdminRoute(deps, w, r)
	if !ok {
		return core.Principal{}, false
	}
	if deps.CSRF == nil || deps.CSRF.Verify(r) != nil {
		http.Error(w, "invalid CSRF token", http.StatusForbidden)
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
	slices.SortFunc(data.Regions, func(a, b estateRow) int { return strings.Compare(strings.ToLower(a.Region), strings.ToLower(b.Region)) })
	slices.SortFunc(data.Plans, func(a, b core.Plan) int { return strings.Compare(strings.ToLower(a.Name), strings.ToLower(b.Name)) })
}
