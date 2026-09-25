package access

import (
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"slices"
	"strconv"
	"strings"
	"time"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/organization"
)

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

type projectView struct {
	ID           string
	Name         string
	Region       string
	BackupRegion string
	Selected     bool
	CreatedAt    string
}

type invitationView struct {
	ID        string
	Email     string
	Role      string
	Status    string
	ExpiresAt string
}

type pageData struct {
	OrganizationName   string
	CurrentUserID      string
	CanManage          bool
	MemberCount        int
	ProjectCount       int
	PendingInviteCount int
	Members            []memberView
	Projects           []projectView
	Invitations        []invitationView
	Tab                string
	Query              string
	RoleFilter         string
	Page               int
	PageCount          int
	Total              int
	First              int
	Last               int
	PreviousURL        string
	NextURL            string
	Error              string
	InviteURL          string
}

type invitationPageData struct {
	OrganizationName string
	Email            string
	Role             string
	ExpiresAt        string
	Token            string
	CurrentEmail     string
	HasSession       bool
	CanRespond       bool
}

func Mount(mux *http.ServeMux, deps core.Deps) {
	organizationService := organization.NewService(deps.Store)
	service := NewService(deps.Store)
	mux.HandleFunc("GET /access", accessPage(deps, organizationService))
	mux.HandleFunc("POST /api/v1/access/invitations", createInvitation(deps, organizationService))
	mux.HandleFunc("POST /api/v1/access/invitations/{invitationID}/resend", resendInvitation(deps, organizationService))
	mux.HandleFunc("POST /api/v1/access/invitations/{invitationID}/revoke", revokeInvitation(deps, organizationService))
	mux.HandleFunc("POST /api/v1/access/members/{userID}/role", updateMemberRole(deps, organizationService))
	mux.HandleFunc("POST /api/v1/access/members/{userID}/revoke", revokeMember(deps, organizationService))
	mux.HandleFunc("GET /invitations/{token}", invitationPage(deps))
	mux.HandleFunc("POST /invitations/{token}/respond", respondToInvitation(deps, service))
}

func accessPage(deps core.Deps, service organization.Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, ok := deps.Sessions.Principal(r)
		if !ok {
			http.Redirect(w, r, "/login", http.StatusSeeOther)
			return
		}
		renderAccessPage(w, r, deps, service, p, "", r.URL.Query().Get("error"))
	}
}

func renderAccessPage(w http.ResponseWriter, r *http.Request, deps core.Deps, service organization.Service, p core.Principal, inviteURL, message string) {
	account, org, err := service.Snapshot(p)
	if err != nil {
		writeProblem(w, err)
		return
	}
	data := buildPageData(account, org, p, r.URL.Query(), message, inviteURL)
	csrfToken := deps.CSRF.Token(w, r)
	if r.Method == http.MethodGet && strings.EqualFold(r.Header.Get("Datastar-Request"), "true") && r.URL.Query().Get("tab") == "members" {
		if deps.Events == nil {
			http.Error(w, "server events are unavailable", http.StatusInternalServerError)
			return
		}
		var fragment strings.Builder
		if err := accessPageView(core.Page{CSRFToken: csrfToken}, data).Render(r.Context(), &fragment); err != nil {
			http.Error(w, "could not render team page", http.StatusInternalServerError)
			return
		}
		_ = deps.Events.PatchElements(w, r, fragment.String())
		return
	}
	page := core.Page{
		Layout: "shell", Title: "Team · TerraScale", Principal: p, CSRFToken: csrfToken,
		View: func(page core.Page) templ.Component {
			return accessPageView(page, data)
		},
	}
	if err := deps.Render.Page(w, r, page); err != nil {
		http.Error(w, "could not render team page", http.StatusInternalServerError)
	}
}

func buildPageData(account core.Account, org core.Organization, p core.Principal, query url.Values, message, inviteURL string) pageData {
	tab := query.Get("tab")
	if tab != "members" && tab != "projects" && tab != "invitations" {
		if query.Get("q") != "" || query.Get("role") != "" {
			tab = "members"
		} else if query.Get("create") != "" {
			tab = "invitations"
		} else {
			tab = "overview"
		}
	}
	search := strings.TrimSpace(query.Get("q"))
	// The filter form sends the current role first and a chosen option last.
	roleFilter := ""
	if roles := query["role"]; len(roles) > 0 {
		roleFilter = roles[len(roles)-1]
	}
	if roleFilter != "owner" && roleFilter != "member" && roleFilter != "viewer" {
		roleFilter = "all"
	}

	allMembers := make([]memberView, 0, len(org.Members))
	for _, member := range org.Members {
		if member.Status != "active" {
			continue
		}
		if roleFilter != "all" && member.Role != roleFilter {
			continue
		}
		if search != "" && !strings.Contains(strings.ToLower(member.Name), strings.ToLower(search)) && !strings.Contains(strings.ToLower(member.Email), strings.ToLower(search)) {
			continue
		}
		allMembers = append(allMembers, memberView{
			ID: member.UserID, Name: member.Name, Email: member.Email, Role: member.Role,
			IsOwner:  member.UserID == org.OwnerUserID || member.Role == "owner",
			IsSelf:   member.UserID == p.UserID,
			CanEdit:  org.OwnerUserID == p.UserID && member.UserID != p.UserID && member.UserID != org.OwnerUserID && member.Role != "owner",
			JoinedAt: member.CreatedAt.UTC().Format("2006-01-02"),
		})
	}
	slices.SortFunc(allMembers, func(a, b memberView) int { return strings.Compare(a.Name, b.Name) })

	allProjects := make([]projectView, 0, len(org.Projects))
	for _, project := range org.Projects {
		if project.OrganizationID != org.ID {
			continue
		}
		allProjects = append(allProjects, projectView{
			ID: project.ID, Name: project.Name, Region: project.Region, BackupRegion: project.BackupRegion,
			Selected:  project.ID == account.ActiveProjectID,
			CreatedAt: project.CreatedAt.UTC().Format(time.RFC3339),
		})
	}
	slices.SortFunc(allProjects, func(a, b projectView) int { return strings.Compare(a.Name, b.Name) })

	allInvitations := make([]invitationView, 0, len(org.Invitations))
	for _, invitation := range org.Invitations {
		if invitation.Email == "" {
			continue
		}
		allInvitations = append(allInvitations, invitationView{
			ID: invitation.ID, Email: invitation.Email, Role: invitation.Role,
			Status:    invitationStatusLabel(invitation.Status),
			ExpiresAt: invitation.ExpiresAt.UTC().Format(time.RFC3339),
		})
	}
	slices.SortFunc(allInvitations, func(a, b invitationView) int {
		aPending, bPending := strings.EqualFold(a.Status, "pending"), strings.EqualFold(b.Status, "pending")
		if aPending && !bPending {
			return -1
		}
		if !aPending && bPending {
			return 1
		}
		return strings.Compare(a.Email, b.Email)
	})

	const pageSize = 10
	filtered := 0
	switch tab {
	case "members":
		filtered = len(allMembers)
	case "projects":
		filtered = len(allProjects)
	case "invitations":
		filtered = len(allInvitations)
	}
	pageCount := max(1, (filtered+pageSize-1)/pageSize)
	requestedPage := 1
	if parsed, err := parsePositiveInt(query.Get("page")); err == nil {
		requestedPage = parsed
	}
	page := min(max(1, requestedPage), pageCount)
	start := (page - 1) * pageSize
	data := pageData{
		OrganizationName: org.Name, CurrentUserID: p.UserID,
		CanManage:   org.OwnerUserID == p.UserID,
		MemberCount: len(allMembers), ProjectCount: len(allProjects),
		Tab: tab, Query: search, RoleFilter: roleFilter,
		Page: page, PageCount: pageCount, Total: filtered,
		Error: message, InviteURL: inviteURL,
	}
	for _, invitation := range allInvitations {
		if strings.EqualFold(invitation.Status, "Pending") {
			data.PendingInviteCount++
		}
	}
	switch tab {
	case "members":
		data.Members = pageWindow(allMembers, start, pageSize)
	case "projects":
		data.Projects = pageWindow(allProjects, start, pageSize)
	case "invitations":
		data.Invitations = pageWindow(allInvitations, start, pageSize)
	default:
		data.Members = pageWindow(allMembers, 0, pageSize)
		data.Projects = pageWindow(allProjects, 0, pageSize)
		data.Invitations = pageWindow(allInvitations, 0, pageSize)
	}
	if filtered > 0 {
		data.First = start + 1
		data.Last = min(start+pageSize, filtered)
	}
	if page > 1 {
		data.PreviousURL = accessPageURL(tab, search, roleFilter, page-1)
	}
	if page < pageCount {
		data.NextURL = accessPageURL(tab, search, roleFilter, page+1)
	}
	return data
}

func pageWindow[T any](items []T, start, size int) []T {
	if start >= len(items) {
		return nil
	}
	end := min(start+size, len(items))
	return slices.Clone(items[start:end])
}

func parsePositiveInt(value string) (int, error) {
	if value == "" {
		return 1, nil
	}
	var number int
	_, err := fmt.Sscan(value, &number)
	if err != nil || number < 1 {
		return 1, core.ErrInvalid
	}
	return number, nil
}

func accessPageURL(tab, search, role string, page int) string {
	values := make(url.Values)
	values.Set("tab", tab)
	if search != "" {
		values.Set("q", search)
	}
	if role != "all" {
		values.Set("role", role)
	}
	values.Set("page", strconv.Itoa(page))
	return "/access?" + values.Encode()
}

func invitationStatusLabel(status string) string {
	switch strings.ToLower(status) {
	case "pending":
		return "Pending"
	case "accepted":
		return "Accepted"
	case "revoked":
		return "Canceled"
	case "declined":
		return "Declined"
	case "expired":
		return "Expired"
	default:
		return status
	}
}

func createInvitation(deps core.Deps, service organization.Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, form, ok := parseMutation(w, r, deps)
		if !ok {
			return
		}
		role := form.Get("role")
		if role == "" {
			role = "member"
		}
		_, token, err := service.CreateInvitation(p, form.Get("email"), role)
		if err != nil {
			redirectAccessError(w, r, err)
			return
		}
		w.Header().Set("Cache-Control", "no-store")
		w.Header().Set("Referrer-Policy", "no-referrer")
		renderAccessPage(w, r, deps, service, p, "/invitations/"+url.PathEscape(token), "")
	}
}

func resendInvitation(deps core.Deps, service organization.Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, _, ok := parseMutation(w, r, deps)
		if !ok {
			return
		}
		_, token, err := service.ResendInvitation(p, r.PathValue("invitationID"))
		if err != nil {
			redirectAccessError(w, r, err)
			return
		}
		w.Header().Set("Cache-Control", "no-store")
		w.Header().Set("Referrer-Policy", "no-referrer")
		renderAccessPage(w, r, deps, service, p, "/invitations/"+url.PathEscape(token), "")
	}
}

func revokeInvitation(deps core.Deps, service organization.Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, _, ok := parseMutation(w, r, deps)
		if !ok {
			return
		}
		if err := service.RevokeInvitation(p, r.PathValue("invitationID")); err != nil {
			redirectAccessError(w, r, err)
			return
		}
		http.Redirect(w, r, "/access?tab=invitations", http.StatusSeeOther)
	}
}

func updateMemberRole(deps core.Deps, service organization.Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, form, ok := parseMutation(w, r, deps)
		if !ok {
			return
		}
		if err := service.UpdateMemberRole(p, r.PathValue("userID"), form.Get("role")); err != nil {
			redirectAccessError(w, r, err)
			return
		}
		http.Redirect(w, r, "/access?tab=members", http.StatusSeeOther)
	}
}

func revokeMember(deps core.Deps, service organization.Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, _, ok := parseMutation(w, r, deps)
		if !ok {
			return
		}
		_, org, err := service.Snapshot(p)
		if err != nil {
			writeProblem(w, err)
			return
		}
		userID := r.PathValue("userID")
		member, exists := org.Members[userID]
		if err := service.RemoveMember(p, userID); err != nil {
			redirectAccessError(w, r, err)
			return
		}
		if exists {
			if target, found := deps.Store.FindByEmail(member.Email); found {
				deps.Sessions.RevokeUser(target.ID, userID)
			}
		}
		http.Redirect(w, r, "/access?tab=members", http.StatusSeeOther)
	}
}

func invitationPage(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		token := r.PathValue("token")
		org, invitation, ok := deps.Store.FindInvitation(hashInvitationToken(token))
		if !ok {
			http.NotFound(w, r)
			return
		}
		p, signedIn := deps.Sessions.Principal(r)
		currentEmail := ""
		canRespond := false
		if signedIn {
			if account, err := deps.Store.SnapshotAs(p); err == nil {
				currentEmail = account.User.Email
				canRespond = core.NormalizeEmail(currentEmail) == core.NormalizeEmail(invitation.Email)
			}
		}
		w.Header().Set("Cache-Control", "no-store")
		w.Header().Set("Referrer-Policy", "no-referrer")
		page := core.Page{
			Layout: "auth", Title: "Organization invitation · TerraScale",
			Principal: p, CSRFToken: deps.CSRF.Token(w, r),
			View: func(page core.Page) templ.Component {
				return invitationPageView(page, invitationPageData{
					OrganizationName: org.Name, Email: invitation.Email, Role: invitation.Role,
					ExpiresAt: invitation.ExpiresAt.UTC().Format(time.RFC3339),
					Token:     token, CurrentEmail: currentEmail, HasSession: signedIn, CanRespond: canRespond,
				})
			},
		}
		if err := deps.Render.Page(w, r, page); err != nil {
			http.Error(w, "could not render invitation page", http.StatusInternalServerError)
		}
	}
}

func respondToInvitation(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		p, ok := deps.Sessions.Principal(r)
		if !ok {
			http.Error(w, "authentication required", http.StatusUnauthorized)
			return
		}
		r.Body = http.MaxBytesReader(w, r.Body, 1<<20)
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		action := strings.ToLower(strings.TrimSpace(r.Form.Get("action")))
		org, err := service.RespondToInvitation(p, r.PathValue("token"), action)
		if err != nil {
			writeProblem(w, err)
			return
		}
		if action == "accept" {
			refreshed, ok := deps.Store.CurrentPrincipal(p.AccountID, p.UserID)
			if !ok {
				writeProblem(w, core.ErrForbidden)
				return
			}
			if err := deps.Sessions.Start(w, r, refreshed); err != nil {
				http.Error(w, "could not refresh session", http.StatusInternalServerError)
				return
			}
			http.Redirect(w, r, "/access?tab=members&accepted=1", http.StatusSeeOther)
			return
		}
		http.Redirect(w, r, "/access?error="+url.QueryEscape("Invitation to "+org.Name+" declined."), http.StatusSeeOther)
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

func redirectAccessError(w http.ResponseWriter, r *http.Request, err error) {
	message := "The team change could not be completed."
	switch {
	case errors.Is(err, core.ErrInvalid):
		message = "Check the email address, role, or other submitted details."
	case errors.Is(err, core.ErrConflict):
		message = "That person is already a member or has a pending invitation."
	case errors.Is(err, core.ErrForbidden):
		message = "You do not have permission to make that change."
	case errors.Is(err, organization.ErrRequiresOwner):
		message = "Only the organization owner can make that change."
	case errors.Is(err, core.ErrNotFound):
		message = "The selected member or invitation was not found."
	}
	http.Redirect(w, r, "/access?error="+url.QueryEscape(message), http.StatusSeeOther)
}

func writeProblem(w http.ResponseWriter, err error) {
	status := http.StatusBadRequest
	switch {
	case errors.Is(err, core.ErrUnauthorized):
		status = http.StatusUnauthorized
	case errors.Is(err, core.ErrForbidden), errors.Is(err, organization.ErrRequiresOwner):
		status = http.StatusForbidden
	case errors.Is(err, core.ErrNotFound):
		status = http.StatusNotFound
	case errors.Is(err, core.ErrConflict):
		status = http.StatusConflict
	}
	http.Error(w, http.StatusText(status), status)
}
