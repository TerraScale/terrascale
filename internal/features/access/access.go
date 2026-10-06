package access

import (
	"errors"
	"fmt"
	"math"
	"net/http"
	"net/url"
	"slices"
	"strconv"
	"strings"
	"time"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/organization"
	"terrascale.tech/dashboard/internal/features/payments"
)

type memberView struct {
	ID       string `json:"id"`
	Name     string `json:"name"`
	Email    string `json:"email"`
	Role     string `json:"role"`
	IsOwner  bool   `json:"isOwner"`
	IsSelf   bool   `json:"isSelf"`
	CanEdit  bool   `json:"canEdit" jsonschema:"Whether the caller may change this member's role or remove them."`
	JoinedAt string `json:"joinedAt" jsonschema:"Join date as YYYY-MM-DD."`
	// JoinedOn is the join date as the Team page shows it.
	JoinedOn string `json:"-"`
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
	ID        string `json:"id"`
	Email     string `json:"email"`
	Role      string `json:"role"`
	Status    string `json:"status" jsonschema:"Pending, Accepted, Declined, Canceled, or Expired."`
	ExpiresAt string `json:"expiresAt" jsonschema:"RFC 3339 expiry time, empty when the invitation does not expire."`
	// Open marks an invitation still pending in the store, even past its
	// expiry, so an owner can resend or cancel it.
	Open bool `json:"open" jsonschema:"Whether the invitation can still be resent or canceled."`
	// ExpiresOn is the expiry as the Team page shows it.
	ExpiresOn string `json:"-"`
}

// notice confirms an invitation response after its redirect.
type notice struct {
	Tone, Title, Message string
}

// shareView is one colored row of a distribution chart.
type shareView struct {
	Label   string
	Tone    string
	Count   int
	Percent int
}

type pageData struct {
	OrganizationName   string
	CurrentUserID      string
	CanManage          bool
	MemberCount        int
	ProjectCount       int
	PendingInviteCount int
	RoleShares         []shareView
	InvitationShares   []shareView
	InvitationTotal    int
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
	Notice             notice
	InviteURL          string
}

func Mount(mux *http.ServeMux, deps core.Deps, billing *payments.Stripe) {
	organizationService := organization.NewService(deps.Store, deps.Sessions)
	service := NewService(deps.Store, organizationService)
	mountTools(deps.Tools, service)
	mux.HandleFunc("GET /access", func(w http.ResponseWriter, r *http.Request) {
		query := r.URL.Query()
		query.Set("tab", "members")
		http.Redirect(w, r, "/organization?"+query.Encode(), http.StatusSeeOther)
	})
	mux.HandleFunc("GET /organization", func(w http.ResponseWriter, r *http.Request) {
		if tab := r.URL.Query().Get("tab"); tab == "members" || tab == "invitations" {
			accessPage(deps, organizationService)(w, r)
		} else {
			organization.Page(deps, billing)(w, r)
		}
	})
	mux.HandleFunc("POST /api/v1/access/invitations", createInvitation(deps, service))
	mux.HandleFunc("POST /api/v1/access/invitations/{invitationID}/resend", resendInvitation(deps, service))
	mux.HandleFunc("POST /api/v1/access/invitations/{invitationID}/revoke", revokeInvitation(deps, organizationService))
	mux.HandleFunc("POST /api/v1/access/members/{userID}/role", updateMemberRole(deps, organizationService))
	mux.HandleFunc("POST /api/v1/access/members/{userID}/revoke", revokeMember(deps, organizationService))
	mux.HandleFunc("GET /invitations/{token}", invitationPage(deps))
	mux.HandleFunc("POST /invitations/{token}/respond", respondToInvitation(deps, service))
	mux.HandleFunc("POST /invitations/{token}/switch-account", switchInvitationAccount(deps))
}

func accessPage(deps core.Deps, service organization.Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, ok := deps.Sessions.Principal(r)
		if !ok {
			core.RedirectToLogin(w, r)
			return
		}
		renderAccessPage(w, r, deps, service, p, "", r.URL.Query().Get("error"))
	}
}

func renderAccessPage(w http.ResponseWriter, r *http.Request, deps core.Deps, service organization.Service, p core.Principal, inviteURL, message string) {
	account, org, err := service.Snapshot(r.Context(), p)
	if err != nil {
		writeProblem(w, err)
		return
	}
	query := r.URL.Query()
	if query.Get("tab") != "members" && query.Get("tab") != "invitations" {
		query.Set("tab", "members")
	}
	data := buildPageData(account, org, p, query, message, inviteURL, time.Now())
	csrfToken := deps.CSRF.Token(w, r)
	if r.Method == http.MethodGet && strings.EqualFold(r.Header.Get("Datastar-Request"), "true") && r.URL.Query().Get("tab") == "members" {
		if deps.Events == nil {
			http.Error(w, "server events are unavailable", http.StatusInternalServerError)
			return
		}
		var fragment strings.Builder
		if err := accessPageView(core.Page{CSRFToken: csrfToken}, data).Render(r.Context(), &fragment); err != nil {
			http.Error(w, "could not render organization members", http.StatusInternalServerError)
			return
		}
		_ = deps.Events.PatchElements(w, r, fragment.String())
		return
	}
	page := core.Page{
		Layout: "shell", Title: "Organization · TerraScale", Principal: p, CSRFToken: csrfToken,
		View: func(page core.Page) templ.Component {
			return accessPageView(page, data)
		},
	}
	if err := deps.Render.Page(w, r, page); err != nil {
		http.Error(w, "could not render organization members", http.StatusInternalServerError)
	}
}

func buildPageData(account core.Account, org core.Organization, p core.Principal, query url.Values, message, inviteURL string, now time.Time) pageData {
	tab := query.Get("tab")
	if !slices.Contains(listTabs, tab) {
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
	if !slices.Contains(filterRoles[1:], roleFilter) {
		roleFilter = "all"
	}

	allMembers := memberViews(org, p, search, roleFilter)

	allProjects := make([]projectView, 0, len(org.Projects))
	for _, project := range org.Projects {
		if project.OrganizationID != org.ID {
			continue
		}
		allProjects = append(allProjects, projectView{
			ID: project.ID, Name: project.Name, Region: project.Region, BackupRegion: project.BackupRegion,
			Selected:  project.ID == account.ActiveProjectID,
			CreatedAt: displayDay(project.CreatedAt, "Unknown"),
		})
	}
	slices.SortFunc(allProjects, func(a, b projectView) int { return strings.Compare(a.Name, b.Name) })

	allInvitations := invitationViews(org, now)

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
		Error: message, Notice: invitationNotice(query.Get("invitation"), org.Name), InviteURL: inviteURL,
	}
	for _, invitation := range allInvitations {
		if strings.EqualFold(invitation.Status, "Pending") {
			data.PendingInviteCount++
		}
	}
	data.RoleShares = roleShares(org)
	data.InvitationShares = invitationShares(allInvitations)
	data.InvitationTotal = len(allInvitations)
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

// listTabs are the Team page tabs besides the overview.
var listTabs = []string{"members", "projects", "invitations"}

// filterRoles lists the member filter's choices, "all" first.
var filterRoles = []string{"all", "owner", "admin", "member", "viewer"}

// roleShares splits active members by organization role. Every role is
// listed, even when empty, so its color stays put.
func roleShares(org core.Organization) []shareView {
	shares := []shareView{
		{Label: "Organization Owner", Tone: roleTone("owner")},
		{Label: "Admin", Tone: roleTone("admin")},
		{Label: "Member", Tone: roleTone("member")},
		{Label: "Viewer", Tone: roleTone("viewer")},
	}
	total := 0
	for _, member := range org.Members {
		if member.Status != "active" {
			continue
		}
		total++
		switch {
		case member.UserID == org.OwnerUserID || member.Role == "owner":
			shares[0].Count++
		case member.Role == "admin":
			shares[1].Count++
		case member.Role == "member":
			shares[2].Count++
		case member.Role == "viewer":
			shares[3].Count++
		}
	}
	return withPercents(shares, total)
}

// invitationShares counts invitations by status, keeping only statuses in use.
func invitationShares(invitations []invitationView) []shareView {
	var shares []shareView
	for _, status := range []string{"Pending", "Accepted", "Declined", "Canceled", "Expired"} {
		share := shareView{Label: status, Tone: invitationTone(status)}
		for _, invitation := range invitations {
			if invitation.Status == status {
				share.Count++
			}
		}
		if share.Count > 0 {
			shares = append(shares, share)
		}
	}
	return withPercents(shares, len(invitations))
}

func withPercents(shares []shareView, total int) []shareView {
	if total == 0 {
		return shares
	}
	for i := range shares {
		shares[i].Percent = int(math.Round(float64(shares[i].Count) * 100 / float64(total)))
	}
	return shares
}

// roleTone colors an organization role on badges and charts.
func roleTone(role string) string {
	switch role {
	case "owner":
		return "violet"
	case "admin":
		return "teal"
	case "member":
		return "blue"
	default:
		return "slate"
	}
}

// invitationTone colors an invitation status on badges and charts.
func invitationTone(status string) string {
	switch status {
	case "Pending":
		return "amber"
	case "Accepted":
		return "emerald"
	case "Declined":
		return "rose"
	default:
		return "slate"
	}
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
	return "/organization?" + values.Encode()
}

// invitationStatus labels an invitation, reporting a pending one past its
// expiry as expired: its link no longer works.
func invitationStatus(invitation core.Invitation, now time.Time) string {
	if strings.EqualFold(invitation.Status, "pending") && !invitation.ExpiresAt.IsZero() && !now.Before(invitation.ExpiresAt) {
		return "Expired"
	}
	return invitationStatusLabel(invitation.Status)
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

// invitationNotice confirms the invitation response named by the redirect
// that follows it.
func invitationNotice(outcome, organizationName string) notice {
	switch outcome {
	case "accepted":
		return notice{Tone: "success", Title: "Invitation accepted", Message: "You are now a member of " + organizationName + "."}
	case "declined":
		return notice{Tone: "info", Title: "Invitation declined", Message: "You were not added to that organization."}
	default:
		return notice{}
	}
}

// roleLabel capitalizes an organization role for display.
func roleLabel(role string) string {
	role = strings.TrimSpace(role)
	if role == "" {
		return "Unknown"
	}
	return strings.ToUpper(role[:1]) + role[1:]
}

// displayDay formats a date for tables, or returns zero for an unset time.
func displayDay(t time.Time, zero string) string {
	if t.IsZero() {
		return zero
	}
	return t.UTC().Format("Jan 2, 2006")
}

// protectInvitationLink keeps a response that carries an invitation token
// out of caches and keeps its URL from other origins. Unlike no-referrer,
// same-origin still sends Origin on the page's own form posts, which the
// CSRF check requires.
func protectInvitationLink(w http.ResponseWriter) {
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Referrer-Policy", "same-origin")
}

func createInvitation(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, form, ok := parseMutation(w, r, deps)
		if !ok {
			return
		}
		_, link, err := service.Invite(r.Context(), p, form.Get("email"), form.Get("role"))
		if err != nil {
			redirectAccessError(w, r, err)
			return
		}
		protectInvitationLink(w)
		renderAccessPage(w, r, deps, service.organizations, p, link, "")
	}
}

func resendInvitation(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, _, ok := parseMutation(w, r, deps)
		if !ok {
			return
		}
		_, link, err := service.Resend(r.Context(), p, r.PathValue("invitationID"))
		if err != nil {
			redirectAccessError(w, r, err)
			return
		}
		protectInvitationLink(w)
		renderAccessPage(w, r, deps, service.organizations, p, link, "")
	}
}

func revokeInvitation(deps core.Deps, service organization.Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, _, ok := parseMutation(w, r, deps)
		if !ok {
			return
		}
		if err := service.RevokeInvitation(r.Context(), p, r.PathValue("invitationID")); err != nil {
			redirectAccessError(w, r, err)
			return
		}
		http.Redirect(w, r, "/organization?tab=invitations", http.StatusSeeOther)
	}
}

func updateMemberRole(deps core.Deps, service organization.Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, form, ok := parseMutation(w, r, deps)
		if !ok {
			return
		}
		if err := service.UpdateMemberRole(r.Context(), p, r.PathValue("userID"), form.Get("role")); err != nil {
			redirectAccessError(w, r, err)
			return
		}
		http.Redirect(w, r, "/organization?tab=members", http.StatusSeeOther)
	}
}

func revokeMember(deps core.Deps, service organization.Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, _, ok := parseMutation(w, r, deps)
		if !ok {
			return
		}
		if err := service.RemoveMember(r.Context(), p, r.PathValue("userID")); err != nil {
			redirectAccessError(w, r, err)
			return
		}
		http.Redirect(w, r, "/organization?tab=members", http.StatusSeeOther)
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
	case errors.Is(err, organization.ErrMemberLimit):
		message = "Your plan's member limit is reached. Cancel a pending invitation, remove a member, or upgrade the plan."
	case errors.Is(err, core.ErrConflict):
		message = "That person is already a member or has a pending invitation."
	case errors.Is(err, core.ErrForbidden):
		message = "You do not have permission to make that change."
	case errors.Is(err, organization.ErrRequiresOwner):
		message = "Only the organization owner can make that change."
	case errors.Is(err, core.ErrNotFound):
		message = "The selected member or invitation was not found."
	case errors.Is(err, core.ErrUnavailable):
		message = "The team service is unavailable right now. Try again in a moment."
	}
	query := url.Values{"error": {message}}
	if tab := r.URL.Query().Get("tab"); slices.Contains(listTabs, tab) {
		query.Set("tab", tab)
	}
	http.Redirect(w, r, "/organization?"+query.Encode(), http.StatusSeeOther)
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
	case errors.Is(err, core.ErrUnavailable):
		status = http.StatusServiceUnavailable
	}
	http.Error(w, http.StatusText(status), status)
}
