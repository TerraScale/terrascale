// Package home owns the authenticated account overview.
package home

import (
	"embed"
	"errors"
	"io/fs"
	"net/http"
	"slices"
	"strings"

	"terrascale.tech/dashboard/internal/core"
)

//go:embed templates/*.html
var featureTemplates embed.FS

type pageData struct {
	ProjectName         string
	DatabaseCount       int
	MemberCount         int
	ActiveCredentials   int
	UnreadNotifications int
	RecentEvents        []core.AuditEvent
	MCPConfigured       bool
	MonitoringStale     bool
}

// Mount registers the dashboard overview.
func Mount(mux *http.ServeMux, deps core.Deps) {
	templates, err := fs.Sub(featureTemplates, "templates")
	if err != nil {
		panic(err)
	}
	if err := deps.Render.Register("home", templates, "*.html"); err != nil {
		panic(err)
	}

	mux.HandleFunc("GET /", func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/" {
			http.NotFound(w, r)
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
			http.Error(w, "selected project is unavailable", http.StatusForbidden)
			return
		}
		credentials := 0
		for _, key := range project.APIKeys {
			if key.RevokedAt.IsZero() {
				credentials++
			}
		}
		unread := 0
		for _, notification := range account.Notifications {
			if notificationVisible(p, notification) && strings.EqualFold(notification.Status, "unread") {
				unread++
			}
		}
		events := scopedEvents(account.AuditEvents, p)
		slices.SortFunc(events, func(a, b core.AuditEvent) int { return b.OccurredAt.Compare(a.OccurredAt) })
		events = events[:min(5, len(events))]
		data := pageData{
			ProjectName: project.Name, DatabaseCount: len(project.Databases), MemberCount: len(org.Members),
			ActiveCredentials: credentials, UnreadNotifications: unread, RecentEvents: events,
			MCPConfigured: false,
		}
		page := core.Page{
			Template: "home", Layout: "shell", Title: "Overview", Principal: p,
			Data: data, CSRFToken: deps.CSRF.Token(w, r),
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
