package organization

import (
	"cmp"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"slices"
	"strings"
	"time"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/payments"
	"terrascale.tech/dashboard/internal/features/usage"
	sdk "terrascale.tech/sdk"
)

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
	Allowances       []string
	Trial            string
}

type memberView struct {
	ID      string
	Name    string
	Email   string
	Role    string
	IsOwner bool
}

type organizationPageData struct {
	Tab                 string
	Organization        organizationView
	Organizations       []organizationView
	Projects            []projectView
	Members             []memberView
	InvitationCount     int
	SignedInEmail       string
	Regions             []string
	Plans               []planView
	CurrentProjectID    string
	OpenCreateProject   bool
	CanManage           bool
	CanProvision        bool
	CanLeave            bool
	NewOrganization     string
	NewOrganizationID   string
	Error               string
	PaymentCanceled     bool
	Demo                bool
	CanConfirmOwnership bool
	// OwnerName tells a nominee who asked; PendingOwner and TransferExpires
	// tell the owner whom they nominated, while the transfer is still open.
	OwnerName       string
	PendingOwner    memberView
	TransferExpires string
}

// Mount registers organization, project, and organization-scope routes.
func Mount(mux *http.ServeMux, deps core.Deps, billing *payments.Stripe) {
	service := NewService(deps.Store, deps.Sessions)
	mountTools(deps.Tools, service)
	mux.HandleFunc("GET /organization", organizationPage(deps, service, billing))
	mux.HandleFunc("POST /api/v1/dashboard/organization/new-organization", newOrganization(deps, service))
	mux.HandleFunc("POST /api/v1/dashboard/switch-organization", switchOrganization(deps, service))
	mux.HandleFunc("POST /api/v1/dashboard/organization/switch", switchOrganization(deps, service))
	mux.HandleFunc("POST /api/v1/dashboard/switch-project", switchProject(deps, service))
	mux.HandleFunc("POST /api/v1/dashboard/organization/rename", renameOrganization(deps, service))
	mux.HandleFunc("POST /api/v1/dashboard/organization/projects", createProject(deps, service))
	mux.HandleFunc("POST /api/v1/dashboard/organization/projects/{projectID}/rename", renameProject(deps, service))
	mux.HandleFunc("POST /api/v1/dashboard/organization/members/{userID}/role", updateMemberRole(deps, service))
	mux.HandleFunc("POST /api/v1/dashboard/organization/members/{userID}/remove", removeMember(deps, service))
	mux.HandleFunc("POST /api/v1/dashboard/organization/members/ownership", requestOwnershipTransfer(deps, service))
	mux.HandleFunc("POST /api/v1/dashboard/organization/members/ownership/confirm", confirmOwnershipTransfer(deps, service))
	mux.HandleFunc("POST /api/v1/dashboard/organization/members/ownership/cancel", cancelOwnershipTransfer(deps, service))
	mux.HandleFunc("POST /api/v1/dashboard/organization/deletion", requestDeletion(deps, service))
	mux.HandleFunc("POST /api/v1/dashboard/organization/leave", leaveOrganization(deps, service))
}

// wizardPlans lists the paid plans a business organization can start on,
// cheapest first. Checkout grants the plan's trial to an organization that
// never had one.
func wizardPlans(plans []core.Plan, org core.Organization) []planView {
	views := make([]planView, 0, len(plans))
	for _, plan := range plans {
		if !plan.Visible || plan.PriceCents <= 0 {
			continue
		}
		view := planView{
			Key: plan.Key, Name: plan.Name, PriceCents: plan.PriceCents, AnnualPriceCents: plan.AnnualPriceCents,
			MonthlyPrice: fmt.Sprintf("$%.2f", float64(plan.PriceCents)/100),
			AnnualPrice:  fmt.Sprintf("$%.2f", float64(plan.AnnualPriceCents)/100),
			Allowances:   []string{plan.StorageAllowance(), plan.OperationsAllowance(), plan.DatabaseAllowance(), plan.MemberAllowance()},
		}
		if plan.TrialDays > 0 && !org.Billing.TrialUsed {
			view.Trial = fmt.Sprintf("%d-day free trial", plan.TrialDays)
		}
		views = append(views, view)
	}
	slices.SortFunc(views, func(a, b planView) int {
		return cmp.Or(cmp.Compare(a.PriceCents, b.PriceCents), strings.Compare(a.Key, b.Key))
	})
	return views
}

func organizationPage(deps core.Deps, service Service, billing *payments.Stripe) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, ok := deps.Sessions.Principal(r)
		if !ok {
			core.RedirectToLogin(w, r)
			return
		}
		var err error
		var paymentVerified bool
		p, paymentVerified, err = resumeCheckoutOrganization(deps, service, w, r, p)
		if err != nil {
			writeProblem(w, err)
			return
		}
		account, org, err := service.Snapshot(r.Context(), p)
		if err != nil {
			writeProblem(w, err)
			return
		}
		data := makeOrganizationPage(account, org, p, r.URL.Query().Get("newOrganization"), r.URL.Query().Get("error"), time.Now())
		data.Tab = r.URL.Query().Get("tab")
		if !slices.Contains([]string{"organization", "members", "projects", "billing"}, data.Tab) {
			data.Tab = "organization"
		}
		if r.URL.Query().Get("newProject") == "1" {
			data.Tab = "projects"
		}
		if data.Tab == "billing" {
			usage.OrganizationBilling(deps, billing, func(page core.Page, content templ.Component) templ.Component {
				return organizationBillingView(data, content)
			})(w, r)
			return
		}
		data.NewOrganizationID = org.ID
		data.PaymentCanceled = paymentVerified && r.URL.Query().Get("payment") == "canceled" && data.NewOrganization == "payment"
		data.OpenCreateProject = r.URL.Query().Get("newProject") == "1"
		plans, err := deps.Store.Plans(r.Context())
		if err != nil {
			writeProblem(w, err)
			return
		}
		data.Plans = wizardPlans(plans, org)
		data.Demo = deps.Demo
		page := core.Page{
			Layout: "shell", Title: "Organization · TerraScale",
			Principal: p, CSRFToken: deps.CSRF.Token(w, r),
			View: func(page core.Page) templ.Component {
				return organizationPageView(page, data)
			},
		}
		if err := deps.Render.Page(w, r, page); err != nil {
			http.Error(w, "could not render organization page", http.StatusInternalServerError)
		}
	}
}

func resumeCheckoutOrganization(deps core.Deps, service Service, w http.ResponseWriter, r *http.Request, p core.Principal) (core.Principal, bool, error) {
	outcome := r.URL.Query().Get("payment")
	if (outcome != "success" && outcome != "canceled") || r.URL.Query().Get("newOrganization") == "" {
		return p, false, nil
	}
	organizationID := r.URL.Query().Get("organizationID")
	if organizationID == "" {
		return p, false, nil
	}
	target, err := service.ForOrganization(r.Context(), p, organizationID)
	var organization core.Organization
	if err == nil {
		_, organization, err = service.Snapshot(r.Context(), target)
	}
	if err != nil {
		if errors.Is(err, core.ErrNotFound) || errors.Is(err, core.ErrForbidden) || errors.Is(err, core.ErrUnauthorized) {
			return p, false, nil
		}
		return p, false, err
	}
	if organization.Kind != kindBusiness || organization.OwnerUserID != p.UserID {
		return p, false, nil
	}
	checkoutStatus := "complete"
	if outcome == "canceled" {
		checkoutStatus = "canceled"
	}
	returnPath := "/organization?newOrganization=project&organizationID=" + url.QueryEscape(organizationID)
	verified := false
	for _, checkout := range organization.CheckoutSessions {
		if checkout.OwnerUserID == p.UserID && checkout.OrganizationID == organizationID &&
			checkout.Status == checkoutStatus && checkout.ReturnPath == returnPath {
			verified = true
			break
		}
	}
	if !verified {
		return p, false, nil
	}
	if p.OrganizationID == organizationID {
		return p, true, nil
	}
	if err := service.SwitchOrganization(r.Context(), p, organizationID); err != nil {
		return core.Principal{}, false, err
	}
	refreshed, err := refreshPrincipal(deps, w, r, p)
	if err != nil {
		return core.Principal{}, false, err
	}
	return refreshed, true, nil
}

func makeOrganizationPage(account core.Account, active core.Organization, p core.Principal, step, pageError string, now time.Time) organizationPageData {
	data := organizationPageData{
		Organization:        activeOrganizationView(active),
		InvitationCount:     len(active.Invitations),
		CurrentProjectID:    account.ActiveProjectID,
		CanManage:           active.OwnerUserID == p.UserID,
		CanProvision:        canProvision(active, p.UserID),
		CanLeave:            active.Kind != kindPersonal && active.OwnerUserID != p.UserID,
		NewOrganizationID:   active.ID,
		SignedInEmail:       account.User.Email,
		Error:               pageError,
		Regions:             supportedRegions,
		CanConfirmOwnership: active.PendingOwnerUserID == p.UserID && active.PendingOwnerExpiresAt.After(now),
	}
	if owner, ok := active.Members[active.OwnerUserID]; ok {
		data.OwnerName = cmp.Or(owner.Name, owner.Email)
	}
	if nominee, ok := active.Members[active.PendingOwnerUserID]; ok && nominee.Status == statusActive && active.PendingOwnerExpiresAt.After(now) {
		data.PendingOwner = memberView{ID: nominee.UserID, Name: cmp.Or(nominee.Name, nominee.Email), Email: nominee.Email, Role: nominee.Role}
		data.TransferExpires = active.PendingOwnerExpiresAt.UTC().Format("Jan 2, 2006")
	}
	if strings.TrimSpace(step) != "" {
		data.NewOrganization = normalizeWizardStep(step, active)
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
			_, err := service.CreateBusiness(r.Context(), p, form.Get("name"))
			if err != nil {
				redirectWithError(w, r, "/organization?newOrganization=organization", err)
				return
			}
			if _, err := refreshPrincipal(deps, w, r, p); err != nil {
				writeProblem(w, err)
				return
			}
			http.Redirect(w, r, "/organization?newOrganization=payment", http.StatusSeeOther)
		case "payment-method":
			organizationID := form.Get("organizationID")
			if organizationID == "" {
				organizationID = p.OrganizationID
			}
			target, err := service.ForOrganization(r.Context(), p, organizationID)
			if err != nil {
				if !errors.Is(err, core.ErrNotFound) && !errors.Is(err, core.ErrForbidden) && !errors.Is(err, core.ErrUnauthorized) {
					writeProblem(w, err)
					return
				}
				redirectWithError(w, r, "/organization?newOrganization=organization", err)
				return
			}
			_, org, err := service.Snapshot(r.Context(), target)
			if err != nil {
				if errors.Is(err, core.ErrNotFound) {
					redirectWithError(w, r, "/organization?newOrganization=organization", core.ErrForbidden)
				} else {
					writeProblem(w, err)
				}
				return
			}
			if org.Kind != kindBusiness || org.BillingStatus != "billing_required" || org.OwnerUserID != target.UserID {
				redirectWithError(w, r, "/organization?newOrganization=organization", core.ErrForbidden)
				return
			}
			planKey := strings.TrimSpace(form.Get("plan"))
			plan, err := deps.Store.Plan(r.Context(), planKey)
			if err != nil && !errors.Is(err, core.ErrNotFound) {
				writeProblem(w, err)
				return
			}
			if err != nil || !plan.Visible || plan.PriceCents <= 0 {
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
			if err := service.SwitchOrganization(r.Context(), p, organizationID); err != nil {
				writeProblem(w, err)
				return
			}
			if _, err := refreshPrincipal(deps, w, r, p); err != nil {
				writeProblem(w, err)
				return
			}
			// Checkout accepts POST. Preserve this validated form body and the
			// refreshed organization session when forwarding it to billing.
			returnPath := "/organization?newOrganization=project&organizationID=" + url.QueryEscape(organizationID)
			http.Redirect(w, r, "/billing/checkout?return="+url.QueryEscape(returnPath), http.StatusTemporaryRedirect)
		case "project":
			organizationID := form.Get("organizationID")
			if organizationID == "" {
				organizationID = p.OrganizationID
			}
			target, err := service.ForOrganization(r.Context(), p, organizationID)
			if err != nil {
				if !errors.Is(err, core.ErrNotFound) && !errors.Is(err, core.ErrForbidden) && !errors.Is(err, core.ErrUnauthorized) {
					writeProblem(w, err)
					return
				}
				redirectWithError(w, r, "/organization?newOrganization=organization", err)
				return
			}
			_, org, err := service.Snapshot(r.Context(), target)
			if err != nil {
				if errors.Is(err, core.ErrNotFound) {
					redirectWithError(w, r, "/organization?newOrganization=payment", ErrBillingNeeded)
				} else {
					writeProblem(w, err)
				}
				return
			}
			if org.Kind != kindBusiness || org.BillingStatus != "active" || org.OwnerUserID != target.UserID {
				redirectWithError(w, r, "/organization?newOrganization=payment", ErrBillingNeeded)
				return
			}
			_, err = service.CreateProject(r.Context(), target, form.Get("name"), form.Get("region"), form.Get("backupRegion"), form["regions"])
			if err != nil {
				redirectWithError(w, r, "/organization?newOrganization=project", err)
				return
			}
			if err := service.SwitchOrganization(r.Context(), p, target.OrganizationID); err != nil {
				writeProblem(w, err)
				return
			}
			if _, err := refreshPrincipal(deps, w, r, p); err != nil {
				writeProblem(w, err)
				return
			}
			http.Redirect(w, r, "/organization?tab=projects", http.StatusSeeOther)
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
		if err := service.SwitchOrganization(r.Context(), p, form.Get("organizationId")); err != nil {
			writeProblem(w, err)
			return
		}
		if _, err := refreshPrincipal(deps, w, r, p); err != nil {
			writeProblem(w, err)
			return
		}
		fallback := "/organization"
		if r.URL.Path == "/api/v1/dashboard/organization/switch" {
			fallback = "/"
		}
		http.Redirect(w, r, safeReturnURL(form.Get("returnUrl"), fallback), http.StatusSeeOther)
	}
}

func switchProject(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, form, ok := parseMutation(w, r, deps)
		if !ok {
			return
		}
		if err := service.SwitchProject(r.Context(), p, form.Get("projectId")); err != nil {
			writeProblem(w, err)
			return
		}
		if _, err := refreshPrincipal(deps, w, r, p); err != nil {
			writeProblem(w, err)
			return
		}
		http.Redirect(w, r, safeReturnURL(form.Get("returnUrl"), "/"), http.StatusSeeOther)
	}
}

func renameOrganization(deps core.Deps, service Service) http.HandlerFunc {
	return organizationMutation(deps, "/organization", func(p core.Principal, r *http.Request, form url.Values) error {
		return service.RenameOrganization(r.Context(), p, form.Get("name"))
	})
}

func createProject(deps core.Deps, service Service) http.HandlerFunc {
	return organizationMutation(deps, "/organization?tab=projects", func(p core.Principal, r *http.Request, form url.Values) error {
		_, err := service.CreateProject(r.Context(), p, form.Get("name"), form.Get("region"), form.Get("backupRegion"), form["regions"])
		return err
	})
}

func renameProject(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, form, ok := parseMutation(w, r, deps)
		if !ok {
			return
		}
		if err := service.RenameProject(r.Context(), p, r.PathValue("projectID"), form.Get("name"), form.Get("region"), form.Get("backupRegion"), form["regions"]); err != nil {
			redirectWithError(w, r, "/organization?tab=projects", err)
			return
		}
		http.Redirect(w, r, "/organization?tab=projects", http.StatusSeeOther)
	}
}

func updateMemberRole(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, form, ok := parseMutation(w, r, deps)
		if !ok {
			return
		}
		if err := service.UpdateMemberRole(r.Context(), p, r.PathValue("userID"), form.Get("role")); err != nil {
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
		if err := service.RemoveMember(r.Context(), p, r.PathValue("userID")); err != nil {
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
		if err := service.RequestOwnershipTransfer(r.Context(), p, form.Get("userID")); err != nil {
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
		if err := service.ConfirmOwnershipTransfer(r.Context(), p); err != nil {
			redirectWithError(w, r, "/organization", err)
			return
		}
		if _, err := refreshPrincipal(deps, w, r, p); err != nil {
			writeProblem(w, err)
			return
		}
		http.Redirect(w, r, "/organization", http.StatusSeeOther)
	}
}

func cancelOwnershipTransfer(deps core.Deps, service Service) http.HandlerFunc {
	return organizationMutation(deps, "/organization", func(p core.Principal, r *http.Request, _ url.Values) error {
		return service.CancelOwnershipTransfer(r.Context(), p)
	})
}

func requestDeletion(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, form, ok := parseMutation(w, r, deps)
		if !ok {
			return
		}
		if err := service.RequestDeletion(r.Context(), p, form.Get("confirmation")); err != nil {
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
		if err := service.LeaveOrganization(r.Context(), p); err != nil {
			redirectWithError(w, r, "/organization", err)
			return
		}
		if _, err := refreshPrincipal(deps, w, r, p); err != nil {
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
	if !deps.VerifyCSRF(w, r) {
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

// refreshPrincipal re-resolves p from the account and starts a session for
// the result, which it returns.
func refreshPrincipal(deps core.Deps, w http.ResponseWriter, r *http.Request, p core.Principal) (core.Principal, error) {
	refreshed, err := deps.Store.CurrentPrincipal(r.Context(), p.AccountID, p.UserID)
	if errors.Is(err, core.ErrNotFound) {
		return core.Principal{}, core.ErrForbidden
	}
	if err != nil {
		return core.Principal{}, err
	}
	return refreshed, deps.Sessions.Start(w, r, refreshed)
}

func redirectWithError(w http.ResponseWriter, r *http.Request, path string, err error) {
	if errors.Is(err, core.ErrUnavailable) {
		writeProblem(w, err)
		return
	}
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
	case errors.Is(err, ErrNoTransfer):
		message = "That ownership transfer is no longer pending. It expired, was canceled, or went to another member."
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
	case errors.Is(err, core.ErrUnavailable):
		status = http.StatusServiceUnavailable
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

// supportedRegions lists the placement choices offered in project forms.
var supportedRegions = []string{string(sdk.RegionGRU), string(sdk.RegionSFO), string(sdk.RegionFRA)}
