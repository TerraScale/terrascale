package access

import (
	"errors"
	"net/http"
	"net/url"
	"strings"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
)

// invitationPageData describes the invitation landing page. Unavailable marks
// a link that grants nothing: unknown, expired, canceled, or already used.
type invitationPageData struct {
	OrganizationName string
	Email            string
	Role             string
	Expiry           string
	Token            string
	CurrentEmail     string
	HasSession       bool
	CanRespond       bool
	Unavailable      bool
	Error            string
}

func invitationPage(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, signedIn := deps.Sessions.Principal(r)
		data, status := invitationState(deps, p, signedIn, r.PathValue("token"))
		renderInvitation(w, r, deps, p, status, data)
	}
}

func respondToInvitation(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		token := r.PathValue("token")
		p, ok := deps.Sessions.Principal(r)
		if !ok {
			http.Redirect(w, r, invitationLoginURL(token), http.StatusSeeOther)
			return
		}
		r.Body = http.MaxBytesReader(w, r.Body, 1<<20)
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		action := strings.ToLower(strings.TrimSpace(r.Form.Get("action")))
		if _, err := service.RespondToInvitation(p, token, action); err != nil {
			data, status := invitationState(deps, p, true, token)
			if !data.Unavailable {
				data.Error, status = respondError(err, data.OrganizationName)
			}
			renderInvitation(w, r, deps, p, status, data)
			return
		}
		if action != "accept" {
			http.Redirect(w, r, "/access?invitation=declined", http.StatusSeeOther)
			return
		}
		refreshed, ok := deps.Store.CurrentPrincipal(p.AccountID, p.UserID)
		if !ok {
			writeProblem(w, core.ErrForbidden)
			return
		}
		if err := deps.Sessions.Start(w, r, refreshed); err != nil {
			http.Error(w, "could not refresh session", http.StatusInternalServerError)
			return
		}
		http.Redirect(w, r, "/access?tab=members&invitation=accepted", http.StatusSeeOther)
	}
}

// switchInvitationAccount signs a visitor out of the wrong account and sends
// them to sign in again, returning to the invitation afterwards.
func switchInvitationAccount(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		deps.Sessions.End(w, r)
		http.Redirect(w, r, invitationLoginURL(r.PathValue("token")), http.StatusSeeOther)
	}
}

// invitationState loads the invitation behind token for the visitor and the
// HTTP status of its page.
func invitationState(deps core.Deps, p core.Principal, signedIn bool, token string) (invitationPageData, int) {
	data := invitationPageData{Token: token}
	if signedIn {
		if account, err := deps.Store.SnapshotAs(p); err == nil {
			data.HasSession, data.CurrentEmail = true, account.User.Email
		}
	}
	org, invitation, ok := deps.Store.FindInvitation(hashInvitationToken(token))
	if !ok {
		data.Unavailable = true
		return data, http.StatusNotFound
	}
	data.OrganizationName, data.Email = org.Name, invitation.Email
	data.Role = strings.ToLower(roleLabel(invitation.Role))
	data.Expiry = "Does not expire"
	if !invitation.ExpiresAt.IsZero() {
		data.Expiry = "Expires " + displayDay(invitation.ExpiresAt, "")
	}
	data.CanRespond = data.HasSession && core.NormalizeEmail(data.CurrentEmail) == core.NormalizeEmail(invitation.Email)
	return data, http.StatusOK
}

// respondError explains why a still-pending invitation could not be answered.
// A wrong account needs no message: the page already asks to switch.
func respondError(err error, organizationName string) (string, int) {
	switch {
	case errors.Is(err, core.ErrForbidden):
		return "", http.StatusForbidden
	case errors.Is(err, core.ErrConflict):
		return organizationName + " cannot add members right now. It may have reached its plan's member limit or be closing. Ask an organization owner, then try again.", http.StatusConflict
	case errors.Is(err, ErrInvalidInvitationAction):
		return "Choose whether to accept or decline the invitation.", http.StatusBadRequest
	default:
		return "The invitation could not be answered. Try again.", http.StatusInternalServerError
	}
}

func renderInvitation(w http.ResponseWriter, r *http.Request, deps core.Deps, p core.Principal, status int, data invitationPageData) {
	protectInvitationLink(w)
	page := core.Page{
		Layout: "auth", Title: "Organization invitation", Principal: p, CSRFToken: deps.CSRF.Token(w, r), Status: status,
		View: func(page core.Page) templ.Component {
			return invitationPageView(page, data)
		},
	}
	if err := deps.Render.Page(w, r, page); err != nil {
		http.Error(w, "could not render invitation page", http.StatusInternalServerError)
	}
}

func invitationPath(token string) string {
	return "/invitations/" + url.PathEscape(token)
}

func invitationLoginURL(token string) string {
	return "/login?returnTo=" + url.QueryEscape(invitationPath(token))
}
