// Package notifications owns the account-scoped notification inbox.
package notifications

import (
	"net/http"
	"slices"
	"strings"

	"github.com/a-h/templ"

	"terrascale.tech/dashboard/internal/core"
)

const pageSize = 10

type pageData struct {
	Notifications []core.Notification
	Status        string
	Unread        int
	Critical      int
	Total         int
	FilteredCount int
	Page          int
	PageCount     int
	Previous      int
	Next          int
	HasPrevious   bool
	HasNext       bool
}

// Mount registers the notification inbox and its read mutations.
func Mount(mux *http.ServeMux, deps core.Deps) {
	mux.HandleFunc("GET /notifications", func(w http.ResponseWriter, r *http.Request) {
		p, account, ok := signedIn(deps, w, r)
		if !ok {
			return
		}
		all := scoped(account.Notifications, p)
		total, unread, critical := len(all), 0, 0
		for _, item := range all {
			if strings.EqualFold(item.Status, "unread") {
				unread++
				if strings.EqualFold(item.Severity, "danger") || strings.EqualFold(item.Severity, "critical") {
					critical++
				}
			}
		}
		status := "all"
		if strings.EqualFold(r.URL.Query().Get("status"), "unread") {
			status = "unread"
			all = slices.DeleteFunc(all, func(n core.Notification) bool { return !strings.EqualFold(n.Status, "unread") })
		}
		slices.SortFunc(all, func(a, b core.Notification) int { return b.CreatedAt.Compare(a.CreatedAt) })
		count := len(all)
		pages := max(1, (count+pageSize-1)/pageSize)
		page := core.ClampPage(r.URL.Query().Get("page"), pages)
		start := (page - 1) * pageSize
		end := min(start+pageSize, count)
		data := pageData{
			Notifications: all[start:end], Status: status, Unread: unread, Critical: critical,
			Total: total, FilteredCount: count, Page: page, PageCount: pages,
			Previous: max(1, page-1), Next: min(pages, page+1), HasPrevious: page > 1, HasNext: page < pages,
		}
		render(w, r, deps, p, data)
	})

	mux.HandleFunc("POST /api/v1/notifications/{notificationID}/read", func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		p, ok := principal(deps, w, r)
		if !ok {
			return
		}
		id := r.PathValue("notificationID")
		err := deps.Store.UpdateAs(p, func(account *core.Account) error {
			for i := range account.Notifications {
				if account.Notifications[i].ID == id && visible(p, account.Notifications[i]) {
					account.Notifications[i].Status = "Read"
					return nil
				}
			}
			return core.ErrNotFound
		})
		if err != nil {
			writeError(w, err)
			return
		}
		http.Redirect(w, r, "/notifications", http.StatusSeeOther)
	})

	mux.HandleFunc("POST /api/v1/notifications/read-all", func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		p, ok := principal(deps, w, r)
		if !ok {
			return
		}
		err := deps.Store.UpdateAs(p, func(account *core.Account) error {
			for i := range account.Notifications {
				if visible(p, account.Notifications[i]) {
					account.Notifications[i].Status = "Read"
				}
			}
			return nil
		})
		if err != nil {
			writeError(w, err)
			return
		}
		http.Redirect(w, r, "/notifications", http.StatusSeeOther)
	})
}

func signedIn(deps core.Deps, w http.ResponseWriter, r *http.Request) (core.Principal, core.Account, bool) {
	p, ok := principal(deps, w, r)
	if !ok {
		return core.Principal{}, core.Account{}, false
	}
	account, err := deps.Store.SnapshotAs(p)
	if err != nil {
		writeError(w, err)
		return core.Principal{}, core.Account{}, false
	}
	return p, account, true
}

func principal(deps core.Deps, w http.ResponseWriter, r *http.Request) (core.Principal, bool) {
	if deps.Sessions == nil {
		http.Error(w, "authentication required", http.StatusUnauthorized)
		return core.Principal{}, false
	}
	p, ok := deps.Sessions.Principal(r)
	if !ok {
		core.RedirectToLogin(w, r)
		return core.Principal{}, false
	}
	return p, true
}

func render(w http.ResponseWriter, r *http.Request, deps core.Deps, p core.Principal, data pageData) {
	page := core.Page{
		Layout: "shell", Title: "Notifications", Principal: p,
		View: func(view core.Page) templ.Component { return notificationsView(data, view.CSRFToken) },
	}
	if err := deps.Render.Page(w, r, page); err != nil {
		http.Error(w, "could not render notifications", http.StatusInternalServerError)
	}
}

func scoped(items []core.Notification, p core.Principal) []core.Notification {
	result := make([]core.Notification, 0, len(items))
	for _, item := range items {
		if visible(p, item) {
			result = append(result, item)
		}
	}
	return result
}

func visible(p core.Principal, item core.Notification) bool {
	return (item.OrganizationID == "" || item.OrganizationID == p.OrganizationID) &&
		(item.ProjectID == "" || item.ProjectID == p.ProjectID)
}

func writeError(w http.ResponseWriter, err error) {
	if !core.WriteAccessError(w, err) {
		http.Error(w, "invalid request", http.StatusBadRequest)
	}
}
