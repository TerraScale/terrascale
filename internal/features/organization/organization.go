package organization

import (
	"embed"
	"errors"
	"fmt"
	"io/fs"
	"net/http"
	"net/url"
	"slices"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	sdk "terrascale.tech/sdk"
)

//go:embed templates/*.html
var featureTemplates embed.FS

type organizationView struct {
	ID                string
	Name              string
	Kind              string
	Status            string
	Slug              string
	PlanID            string
	BillingStatus     string
	CreatedAt         string
	DeletionRequested bool
}

type projectView struct {
	ID            string
	Name          string
	Slug          string
	Region        string
	BackupRegion  string
	Regions       []string
	RegionOptions []regionView
	CreatedAt     string
	Selected      bool
}

type regionView struct {
	Code     string
	Selected bool
}

type planView struct {
	Key              string
	Name             string
	PriceCents       int64
	AnnualPriceCents int64
	MonthlyPrice     string
	AnnualPrice      string
}

type memberView struct {
	ID       string
	Name     string
	Email    string
	Role     string
	IsOwner  bool
	IsSelf   bool
	CanEdit  bool
	JoinedAt string
}

type organizationPageData struct {
	Organization        organizationView
	Organizations       []organizationView
	Projects            []projectView
	Members             []memberView
	Regions             []string
	Plans               []planView
	CurrentProjectID    string
	CanManage           bool
	CanProvision        bool
	CanLeave            bool
	NewOrganization     string
	NewOrganizationID   string
	Error               string
	OwnershipRequested  bool
	CanConfirmOwnership bool
}

// Mount registers organization, project, and organization-scope routes.
func Mount(mux *http.ServeMux, deps core.Deps) {
	templates, err := fs.Sub(featureTemplates, "templates")
	if err != nil {
		panic(err)
	}
	if err := deps.Render.Register("organization", templates, "organization.html"); err != nil {
		panic(err)
	}

	service := NewService(deps.Store)
	mux.HandleFunc("GET /organization", organizationPage(deps, service))
	mux.HandleFunc("POST /api/v1/dashboard/organization/new-organization", newOrganization(deps, service))
	mux.HandleFunc("POST /api/v1/dashboard/switch-organization", switchOrganization(deps, service))
	mux.HandleFunc("POST /api/v1/dashboard/switch-project", switchProject(deps, service))
	mux.HandleFunc("POST /api/v1/dashboard/organization/rename", renameOrganization(deps, service))
	mux.HandleFunc("POST /api/v1/dashboard/organization/projects", createProject(deps, service))
	mux.HandleFunc("POST /api/v1/dashboard/organization/projects/{projectID}/rename", renameProject(deps, service))
	mux.HandleFunc("POST /api/v1/dashboard/organization/members/{userID}/role", updateMemberRole(deps, service))
	mux.HandleFunc("POST /api/v1/dashboard/organization/members/{userID}/remove", removeMember(deps, service))
	mux.HandleFunc("POST /api/v1/dashboard/organization/members/ownership", requestOwnershipTransfer(deps, service))
	mux.HandleFunc("POST /api/v1/dashboard/organization/members/ownership/confirm", confirmOwnershipTransfer(deps, service))
	mux.HandleFunc("POST /api/v1/dashboard/organization/deletion", requestDeletion(deps, service))
	mux.HandleFunc("POST /api/v1/dashboard/organization/leave", leaveOrganization(deps, service))
}

func organizationPage(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, ok := deps.Sessions.Principal(r)
		if !ok {
			http.Redirect(w, r, "/login", http.StatusSeeOther)
			return
		}
		account, org, err := service.Snapshot(p)
		if err != nil {
			writeProblem(w, err)
			return
		}
		data := makeOrganizationPage(account, org, p, r.URL.Query().Get("newOrganization"), r.URL.Query().Get("error"))
		data.NewOrganizationID = org.ID
		for _, plan := range deps.Store.Plans() {
			if plan.Visible && plan.PriceCents > 0 {
				data.Plans = append(data.Plans, planView{
					Key: plan.Key, Name: plan.Name, PriceCents: plan.PriceCents, AnnualPriceCents: plan.AnnualPriceCents,
					MonthlyPrice: fmt.Sprintf("$%.2f", float64(plan.PriceCents)/100),
					AnnualPrice:  fmt.Sprintf("$%.2f", float64(plan.AnnualPriceCents)/100),
				})
			}
		}
		slices.SortFunc(data.Plans, func(a, b planView) int { return strings.Compare(a.Key, b.Key) })
		page := core.Page{
			Template: "organization", Layout: "shell", Title: "Organization · TerraScale",
			Principal: p, CSRFToken: deps.CSRF.Token(w, r), Data: data,
		}
		if err := deps.Render.Page(w, r, page); err != nil {
			http.Error(w, "could not render organization page", http.StatusInternalServerError)
		}
	}
}

func makeOrganizationPage(account core.Account, active core.Organization, p core.Principal, step, pageError string) organizationPageData {
	data := organizationPageData{
		Organization:        activeOrganizationView(active),
		CurrentProjectID:    account.ActiveProjectID,
		CanManage:           active.OwnerUserID == p.UserID,
		CanProvision:        canProvision(active, p.UserID),
		CanLeave:            active.Kind != kindPersonal && active.OwnerUserID != p.UserID,
		NewOrganizationID:   active.ID,
		NewOrganization:     normalizeWizardStep(step, active),
		Error:               pageError,
		Regions:             supportedRegions(),
		OwnershipRequested:  active.PendingOwnerUserID != "",
		CanConfirmOwnership: active.PendingOwnerUserID == p.UserID && active.PendingOwnerExpiresAt.After(time.Now()),
	}
	for _, org := range account.Organizations {
		if _, err := account.OrganizationFor(p, org.ID); err == nil && strings.EqualFold(org.Status, statusActive) {
			data.Organizations = append(data.Organizations, activeOrganizationView(org))
		}
	}
	slices.SortFunc(data.Organizations, func(a, b organizationView) int { return strings.Compare(a.Name, b.Name) })
	for _, project := range active.Projects {
		if project.OrganizationID != active.ID {
			continue
		}
		projectData := projectView{
			ID: project.ID, Name: project.Name, Slug: project.Slug, Region: project.Region,
			BackupRegion: project.BackupRegion, Regions: slices.Clone(project.Regions),
			CreatedAt: project.CreatedAt.UTC().Format(time.RFC3339), Selected: project.ID == account.ActiveProjectID,
		}
		for _, region := range data.Regions {
			projectData.RegionOptions = append(projectData.RegionOptions, regionView{Code: region, Selected: slices.Contains(project.Regions, region)})
		}
		data.Projects = append(data.Projects, projectData)
	}
	slices.SortFunc(data.Projects, func(a, b projectView) int { return strings.Compare(a.Name, b.Name) })
	for _, member := range active.Members {
		if member.Status != statusActive {
			continue
		}
		data.Members = append(data.Members, memberView{
			ID: member.UserID, Name: member.Name, Email: member.Email, Role: member.Role,
			IsOwner: member.UserID == active.OwnerUserID || member.Role == roleOwner,
			IsSelf:  member.UserID == p.UserID, CanEdit: data.CanManage && member.UserID != p.UserID && member.UserID != active.OwnerUserID,
			JoinedAt: member.CreatedAt.UTC().Format("2006-01-02"),
		})
	}
	slices.SortFunc(data.Members, func(a, b memberView) int { return strings.Compare(a.Name, b.Name) })
	return data
}

func activeOrganizationView(org core.Organization) organizationView {
	return organizationView{
		ID: org.ID, Name: org.Name, Kind: org.Kind, Status: org.Status, Slug: org.Slug,
		PlanID: org.PlanID, BillingStatus: org.BillingStatus,
		CreatedAt: org.CreatedAt.UTC().Format(time.RFC3339), DeletionRequested: org.DeletionRequested,
	}
}

func normalizeWizardStep(requested string, org core.Organization) string {
	organizationReady := org.Kind == kindBusiness && org.BillingStatus == "billing_required"
	billingReady := org.Kind == kindBusiness && org.BillingStatus == "active"
	switch requested {
	case "payment", "payment-method":
		if organizationReady {
			return "payment"
		}
	case "project":
		if billingReady {
			return "project"
		}
		if organizationReady {
			return "payment"
		}
	}
	return "organization"
}

func newOrganization(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, form, ok := parseMutation(w, r, deps)
		if !ok {
			return
		}
		switch form.Get("step") {
		case "organization":
			_, err := service.CreateBusiness(p, form.Get("name"))
			if err != nil {
				redirectWithError(w, r, "/organization?newOrganization=organization", err)
				return
			}
			if err := refreshPrincipal(deps, w, r, p); err != nil {
				writeProblem(w, err)
				return
			}
			http.Redirect(w, r, "/organization?newOrganization=payment", http.StatusSeeOther)
		case "payment-method":
			organizationID := form.Get("organizationID")
			if organizationID == "" {
				organizationID = p.OrganizationID
			}
			target, err := service.ForOrganization(p, organizationID)
			if err != nil {
				redirectWithError(w, r, "/organization?newOrganization=organization", err)
				return
			}
			_, org, err := service.Snapshot(target)
			if err != nil || org.Kind != kindBusiness || org.BillingStatus != "billing_required" || org.OwnerUserID != target.UserID {
				redirectWithError(w, r, "/organization?newOrganization=organization", core.ErrForbidden)
				return
			}
			planKey := strings.TrimSpace(form.Get("plan"))
			plan, exists := deps.Store.Plan(planKey)
			if !exists || !plan.Visible || plan.PriceCents <= 0 {
				redirectWithError(w, r, "/organization?newOrganization=payment", core.ErrInvalid)
				return
			}
			interval := form.Get("interval")
			if interval != "annual" {
				interval = "monthly"
			}
			// Checkout reads the request principal to choose its tenant. Restore the
			// wizard's server-validated organization first so a stale tab cannot
			// charge a different workspace after an organization switch.
			if err := service.SwitchOrganization(p, organizationID); err != nil {
				writeProblem(w, err)
				return
			}
			if err := refreshPrincipal(deps, w, r, p); err != nil {
				writeProblem(w, err)
				return
			}
			returnPath := "/organization?newOrganization=project&payment=success"
			http.Redirect(w, r, "/billing/checkout?plan="+url.QueryEscape(planKey)+"&interval="+url.QueryEscape(interval)+"&return="+url.QueryEscape(returnPath), http.StatusSeeOther)
		case "project":
			organizationID := form.Get("organizationID")
			if organizationID == "" {
				organizationID = p.OrganizationID
			}
			target, err := service.ForOrganization(p, organizationID)
			if err != nil {
				redirectWithError(w, r, "/organization?newOrganization=organization", err)
				return
			}
			_, org, err := service.Snapshot(target)
			if err != nil || org.Kind != kindBusiness || org.BillingStatus != "active" || org.OwnerUserID != target.UserID {
				redirectWithError(w, r, "/organization?newOrganization=payment", ErrBillingNeeded)
				return
			}
			_, err = service.CreateProject(target, form.Get("name"), form.Get("region"), form.Get("backupRegion"), form["regions"])
			if err != nil {
				redirectWithError(w, r, "/organization?newOrganization=project", err)
				return
			}
			if account, ok := deps.Store.Snapshot(p.AccountID); ok && account.ActiveOrganizationID == target.OrganizationID {
				if err := refreshPrincipal(deps, w, r, p); err != nil {
					writeProblem(w, err)
					return
				}
			}
			http.Redirect(w, r, "/organization", http.StatusSeeOther)
		default:
			http.Redirect(w, r, "/organization?newOrganization=organization", http.StatusSeeOther)
		}
	}
}

func switchOrganization(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, form, ok := parseMutation(w, r, deps)
		if !ok {
			return
		}
		if err := service.SwitchOrganization(p, form.Get("organizationId")); err != nil {
			writeProblem(w, err)
			return
		}
		if err := refreshPrincipal(deps, w, r, p); err != nil {
			writeProblem(w, err)
			return
		}
		http.Redirect(w, r, safeReturnURL(form.Get("returnUrl"), "/organization"), http.StatusSeeOther)
	}
}

func switchProject(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, form, ok := parseMutation(w, r, deps)
		if !ok {
			return
		}
		if err := service.SwitchProject(p, form.Get("projectId")); err != nil {
			writeProblem(w, err)
			return
		}
		if err := refreshPrincipal(deps, w, r, p); err != nil {
			writeProblem(w, err)
			return
		}
		http.Redirect(w, r, safeReturnURL(form.Get("returnUrl"), "/"), http.StatusSeeOther)
	}
}

func renameOrganization(deps core.Deps, service Service) http.HandlerFunc {
	return organizationMutation(deps, "/organization", func(p core.Principal, _ *http.Request, form url.Values) error {
		return service.RenameOrganization(p, form.Get("name"))
	})
}

func createProject(deps core.Deps, service Service) http.HandlerFunc {
	return organizationMutation(deps, "/organization", func(p core.Principal, _ *http.Request, form url.Values) error {
		_, err := service.CreateProject(p, form.Get("name"), form.Get("region"), form.Get("backupRegion"), form["regions"])
		return err
	})
}

func renameProject(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, form, ok := parseMutation(w, r, deps)
		if !ok {
			return
		}
		if err := service.RenameProject(p, r.PathValue("projectID"), form.Get("name"), form.Get("region"), form.Get("backupRegion"), form["regions"]); err != nil {
			redirectWithError(w, r, "/organization", err)
			return
		}
		http.Redirect(w, r, "/organization", http.StatusSeeOther)
	}
}

func updateMemberRole(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, form, ok := parseMutation(w, r, deps)
		if !ok {
			return
		}
		if err := service.UpdateMemberRole(p, r.PathValue("userID"), form.Get("role")); err != nil {
			redirectWithError(w, r, "/organization", err)
			return
		}
		http.Redirect(w, r, "/organization", http.StatusSeeOther)
	}
}

func removeMember(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, _, ok := parseMutation(w, r, deps)
		if !ok {
			return
		}
		if err := service.RemoveMember(p, r.PathValue("userID")); err != nil {
			redirectWithError(w, r, "/organization", err)
			return
		}
		http.Redirect(w, r, "/organization", http.StatusSeeOther)
	}
}

func requestOwnershipTransfer(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, form, ok := parseMutation(w, r, deps)
		if !ok {
			return
		}
		if err := service.RequestOwnershipTransfer(p, form.Get("userID")); err != nil {
			redirectWithError(w, r, "/organization", err)
			return
		}
		http.Redirect(w, r, "/organization", http.StatusSeeOther)
	}
}

func confirmOwnershipTransfer(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, _, ok := parseMutation(w, r, deps)
		if !ok {
			return
		}
		if err := service.ConfirmOwnershipTransfer(p); err != nil {
			redirectWithError(w, r, "/organization", err)
			return
		}
		if err := refreshPrincipal(deps, w, r, p); err != nil {
			writeProblem(w, err)
			return
		}
		http.Redirect(w, r, "/organization", http.StatusSeeOther)
	}
}

func requestDeletion(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, form, ok := parseMutation(w, r, deps)
		if !ok {
			return
		}
		if err := service.RequestDeletion(p, form.Get("confirmation")); err != nil {
			redirectWithError(w, r, "/organization", err)
			return
		}
		http.Redirect(w, r, "/organization", http.StatusSeeOther)
	}
}

func leaveOrganization(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, _, ok := parseMutation(w, r, deps)
		if !ok {
			return
		}
		if err := service.LeaveOrganization(p); err != nil {
			redirectWithError(w, r, "/organization", err)
			return
		}
		if err := refreshPrincipal(deps, w, r, p); err != nil {
			writeProblem(w, err)
			return
		}
		http.Redirect(w, r, "/organization", http.StatusSeeOther)
	}
}

func organizationMutation(deps core.Deps, destination string, operation func(core.Principal, *http.Request, url.Values) error) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, form, ok := parseMutation(w, r, deps)
		if !ok {
			return
		}
		if err := operation(p, r, form); err != nil {
			redirectWithError(w, r, destination, err)
			return
		}
		http.Redirect(w, r, destination, http.StatusSeeOther)
	}
}

func parseMutation(w http.ResponseWriter, r *http.Request, deps core.Deps) (core.Principal, url.Values, bool) {
	if err := deps.CSRF.Verify(r); err != nil {
		http.Error(w, "invalid CSRF token", http.StatusForbidden)
		return core.Principal{}, nil, false
	}
	p, ok := deps.Sessions.Principal(r)
	if !ok {
		http.Error(w, "authentication required", http.StatusUnauthorized)
		return core.Principal{}, nil, false
	}
	r.Body = http.MaxBytesReader(w, r.Body, 1<<20)
	if err := r.ParseForm(); err != nil {
		http.Error(w, "invalid form", http.StatusBadRequest)
		return core.Principal{}, nil, false
	}
	return p, r.Form, true
}

func refreshPrincipal(deps core.Deps, w http.ResponseWriter, r *http.Request, p core.Principal) error {
	account, ok := deps.Store.Snapshot(p.AccountID)
	if !ok || account.User.ID != p.UserID {
		return core.ErrForbidden
	}
	return deps.Sessions.Start(w, r, account.Principal())
}

func redirectWithError(w http.ResponseWriter, r *http.Request, path string, err error) {
	message := "The organization change could not be completed."
	switch {
	case errors.Is(err, core.ErrInvalid):
		message = "Check the submitted organization details and try again."
	case errors.Is(err, core.ErrConflict):
		message = "That name or invitation is already in use."
	case errors.Is(err, ErrBillingNeeded):
		message = "Complete Business billing before creating resources."
	case errors.Is(err, ErrPersonalOrg):
		message = "Personal workspaces cannot be left or deleted."
	case errors.Is(err, ErrRequiresOwner):
		message = "Only an organization owner can make that change."
	case errors.Is(err, core.ErrForbidden):
		message = "You do not have permission to make that change."
	case errors.Is(err, core.ErrNotFound):
		message = "The selected organization item was not found."
	}
	separator := "?"
	if strings.Contains(path, "?") {
		separator = "&"
	}
	http.Redirect(w, r, path+separator+"error="+url.QueryEscape(message), http.StatusSeeOther)
}

func writeProblem(w http.ResponseWriter, err error) {
	status := http.StatusBadRequest
	switch {
	case errors.Is(err, core.ErrUnauthorized):
		status = http.StatusUnauthorized
	case errors.Is(err, core.ErrForbidden), errors.Is(err, ErrRequiresOwner):
		status = http.StatusForbidden
	case errors.Is(err, core.ErrNotFound):
		status = http.StatusNotFound
	case errors.Is(err, core.ErrConflict):
		status = http.StatusConflict
	}
	http.Error(w, http.StatusText(status), status)
}

func safeReturnURL(candidate, fallback string) string {
	if candidate == "" {
		return fallback
	}
	parsed, err := url.Parse(candidate)
	if err != nil || parsed.IsAbs() || parsed.Host != "" || !strings.HasPrefix(parsed.Path, "/") || strings.HasPrefix(parsed.Path, "//") || strings.ContainsRune(parsed.Path, '\\') {
		return fallback
	}
	return parsed.RequestURI()
}

func supportedRegions() []string {
	return []string{string(sdk.RegionGRU), string(sdk.RegionSFO), string(sdk.RegionFRA)}
}
