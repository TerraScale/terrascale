// Package notifications owns the account-scoped notification inbox.
package notifications

import (
	"errors"
	"net/http"
	"slices"
	"strconv"
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
		status := "all"
		if strings.EqualFold(r.URL.Query().Get("status"), "unread") {
			status = "unread"
			all = slices.DeleteFunc(all, func(n core.Notification) bool { return !strings.EqualFold(n.Status, "unread") })
		}
		slices.SortFunc(all, func(a, b core.Notification) int { return b.CreatedAt.Compare(a.CreatedAt) })
		unread, critical := 0, 0
		for _, item := range scoped(account.Notifications, p) {
			if strings.EqualFold(item.Status, "unread") {
				unread++
				if strings.EqualFold(item.Severity, "danger") || strings.EqualFold(item.Severity, "critical") {
					critical++
				}
			}
		}
		count := len(all)
		pages := max(1, (count+pageSize-1)/pageSize)
		page := clampPage(r.URL.Query().Get("page"), pages)
		start := (page - 1) * pageSize
		end := min(start+pageSize, count)
		data := pageData{
			Notifications: all[start:end], Status: status, Unread: unread, Critical: critical,
			Total: len(scoped(account.Notifications, p)), FilteredCount: count, Page: page, PageCount: pages,
			Previous: max(1, page-1), Next: min(pages, page+1), HasPrevious: page > 1, HasNext: page < pages,
		}
		render(w, r, deps, p, data)
	})

	mux.HandleFunc("POST /api/v1/notifications/{notificationID}/read", func(w http.ResponseWriter, r *http.Request) {
		if !verifyCSRF(deps, w, r) {
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
		if !verifyCSRF(deps, w, r) {
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
		http.Redirect(w, r, "/login", http.StatusSeeOther)
		return core.Principal{}, false
	}
	return p, true
}

func verifyCSRF(deps core.Deps, w http.ResponseWriter, r *http.Request) bool {
	if deps.CSRF == nil || deps.CSRF.Verify(r) != nil {
		http.Error(w, "invalid CSRF token", http.StatusForbidden)
		return false
	}
	return true
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

func clampPage(raw string, pages int) int {
	page, err := strconv.Atoi(raw)
	if err != nil || page < 1 {
		return 1
	}
	return min(page, pages)
}

func writeError(w http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, core.ErrUnauthorized):
		http.Error(w, "authentication required", http.StatusUnauthorized)
	case errors.Is(err, core.ErrForbidden):
		http.Error(w, "forbidden", http.StatusForbidden)
	case errors.Is(err, core.ErrNotFound):
		http.Error(w, "not found", http.StatusNotFound)
	default:
		http.Error(w, "invalid request", http.StatusBadRequest)
	}
}
