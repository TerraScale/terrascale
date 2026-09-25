// Package home owns the authenticated account overview.
package home

import (
	"errors"
	"net/http"
	"slices"
	"strings"

	"github.com/a-h/templ"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/notfound"
)

type pageData struct {
	Tab                       string
	ProjectName               string
	ActivityRange             string
	DatabaseDetail            string
	OrganizationMembersDetail string
	NotificationsDetail       string
	DatabaseCount             int
	OrganizationMembers       int
	UnreadNotifications       int
	TotalNotifications        int
	ActiveCredentials         int
	ActivityEvents            int
}

// Mount registers the dashboard overview.
func Mount(mux *http.ServeMux, deps core.Deps) {
	mux.HandleFunc("GET /", func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/" {
			notfound.Render(w, r)
			return
		}
		p, account, ok := signedIn(deps, w, r)
		if !ok {
			return
		}
		org, ok := account.Organizations[p.OrganizationID]
		if !ok {
			http.Error(w, "selected organization is unavailable", http.StatusForbidden)
			return
		}
		project, ok := org.Projects[p.ProjectID]
		if !ok {
			if p.ProjectID == "" && account.ActiveProjectID == "" &&
				p.OrganizationID == account.ActiveOrganizationID &&
				org.Kind == "business" && org.Status == "active" &&
				!org.Archived && !org.DeletionRequested && len(org.Projects) == 0 &&
				org.OwnerUserID == p.UserID &&
				(org.BillingStatus == "billing_required" || org.BillingStatus == "active") {
				http.Redirect(w, r, "/organization?newOrganization=project", http.StatusSeeOther)
				return
			}
			http.Error(w, "selected project is unavailable", http.StatusForbidden)
			return
		}
		credentials := 0
		for _, key := range project.APIKeys {
			if key.RevokedAt.IsZero() {
				credentials++
			}
		}
		organizationMembers, ownerIncluded := 0, false
		for userID, member := range org.Members {
			if member.Status == "" || strings.EqualFold(member.Status, "active") {
				organizationMembers++
				if userID == org.OwnerUserID {
					ownerIncluded = true
				}
			}
		}
		if !ownerIncluded && org.OwnerUserID != "" {
			organizationMembers++
		}
		notificationCount, unread := 0, 0
		for _, notification := range account.Notifications {
			if notificationVisible(p, notification) {
				notificationCount++
				if strings.EqualFold(notification.Status, "unread") {
					unread++
				}
			}
		}
		tab := "overview"
		if r.URL.Query().Get("tab") == "connect" {
			tab = "connect"
		}
		activityRange := r.URL.Query().Get("range")
		if !slices.Contains([]string{"24h", "7d", "30d"}, activityRange) {
			activityRange = "7d"
		}
		data := pageData{
			Tab: tab, ProjectName: project.Name, ActivityRange: activityRange,
			DatabaseCount: len(project.Databases), OrganizationMembers: organizationMembers,
			UnreadNotifications: unread, TotalNotifications: notificationCount,
			ActiveCredentials: credentials, ActivityEvents: len(scopedEvents(account.AuditEvents, p)),
			DatabaseDetail: "saved project databases", OrganizationMembersDetail: "active organization members",
			NotificationsDetail: "notifications loaded",
		}
		if data.DatabaseCount == 0 {
			data.DatabaseDetail = "none saved"
		}
		if data.OrganizationMembers == 1 {
			data.OrganizationMembersDetail = "active organization member"
		}
		page := core.Page{
			Layout: "shell", Title: "Overview", Principal: p,
			View: func(core.Page) templ.Component { return homeView(data) },
		}
		if err := deps.Render.Page(w, r, page); err != nil {
			http.Error(w, "could not render overview", http.StatusInternalServerError)
		}
	})
}

func signedIn(deps core.Deps, w http.ResponseWriter, r *http.Request) (core.Principal, core.Account, bool) {
	if deps.Sessions == nil {
		http.Error(w, "authentication required", http.StatusUnauthorized)
		return core.Principal{}, core.Account{}, false
	}
	p, ok := deps.Sessions.Principal(r)
	if !ok {
		http.Redirect(w, r, "/login", http.StatusSeeOther)
		return core.Principal{}, core.Account{}, false
	}
	account, err := deps.Store.SnapshotAs(p)
	if err != nil {
		switch {
		case errors.Is(err, core.ErrForbidden):
			http.Error(w, "forbidden", http.StatusForbidden)
		case errors.Is(err, core.ErrNotFound):
			http.Error(w, "not found", http.StatusNotFound)
		default:
			http.Error(w, "invalid request", http.StatusBadRequest)
		}
		return core.Principal{}, core.Account{}, false
	}
	return p, account, true
}

func notificationVisible(p core.Principal, item core.Notification) bool {
	return (item.OrganizationID == "" || item.OrganizationID == p.OrganizationID) &&
		(item.ProjectID == "" || item.ProjectID == p.ProjectID)
}

func scopedEvents(events []core.AuditEvent, p core.Principal) []core.AuditEvent {
	result := make([]core.AuditEvent, 0, len(events))
	for _, event := range events {
		if (event.OrganizationID == "" || event.OrganizationID == p.OrganizationID) &&
			(event.ProjectID == "" || event.ProjectID == p.ProjectID) {
			result = append(result, event)
		}
	}
	return result
}
