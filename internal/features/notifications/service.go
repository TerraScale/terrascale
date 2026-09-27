package notifications

import (
	"fmt"
	"slices"
	"strings"

	"terrascale.tech/dashboard/internal/core"
)

// Service owns the notification inbox of a principal's selected scope. HTTP
// handlers and MCP tools both call it.
type Service struct {
	store *core.Store
}

// Inbox returns one page of notifications, newest first. status "unread"
// limits the page to unread notifications; any other value lists all.
func (s Service) Inbox(p core.Principal, status, rawPage string) (pageData, error) {
	account, err := s.store.SnapshotAs(p)
	if err != nil {
		return pageData{}, err
	}
	all := slices.DeleteFunc(slices.Clone(account.Notifications), func(n core.Notification) bool { return !n.VisibleTo(p) })
	total, unreadCount, critical := len(all), 0, 0
	for _, item := range all {
		if item.Unread() {
			unreadCount++
			if strings.EqualFold(item.Severity, "danger") || strings.EqualFold(item.Severity, "critical") {
				critical++
			}
		}
	}
	filter := "all"
	if strings.EqualFold(status, "unread") {
		filter = "unread"
		all = slices.DeleteFunc(all, func(n core.Notification) bool { return !n.Unread() })
	}
	slices.SortFunc(all, func(a, b core.Notification) int { return b.CreatedAt.Compare(a.CreatedAt) })
	count := len(all)
	pages := max(1, (count+pageSize-1)/pageSize)
	page := core.ClampPage(rawPage, pages)
	start := (page - 1) * pageSize
	end := min(start+pageSize, count)
	return pageData{
		Notifications: all[start:end], Status: filter, Unread: unreadCount, Critical: critical,
		Total: total, FilteredCount: count, Page: page, PageCount: pages,
		Previous: max(1, page-1), Next: min(pages, page+1), HasPrevious: page > 1, HasNext: page < pages,
	}, nil
}

// MarkRead marks one notification in the selected scope as read and reports
// whether it changed from unread to read.
func (s Service) MarkRead(p core.Principal, notificationID string) (bool, error) {
	marked := false
	err := s.store.UpdateAs(p, func(account *core.Account) error {
		marked = false
		for i := range account.Notifications {
			if account.Notifications[i].ID == notificationID && account.Notifications[i].VisibleTo(p) {
				marked = account.Notifications[i].Unread()
				account.Notifications[i].Status = "Read"
				return nil
			}
		}
		return fmt.Errorf("%w: notification %q", core.ErrNotFound, notificationID)
	})
	if err != nil {
		return false, err
	}
	return marked, nil
}

// MarkAllRead marks every notification in the selected scope as read and
// reports how many were unread.
func (s Service) MarkAllRead(p core.Principal) (int, error) {
	marked := 0
	err := s.store.UpdateAs(p, func(account *core.Account) error {
		marked = 0
		for i := range account.Notifications {
			if account.Notifications[i].VisibleTo(p) {
				if account.Notifications[i].Unread() {
					marked++
				}
				account.Notifications[i].Status = "Read"
			}
		}
		return nil
	})
	return marked, err
}
