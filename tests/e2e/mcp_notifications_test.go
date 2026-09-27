package e2e

import (
	"slices"
	"strings"
	"testing"
)

// invitationNotification is an organization-wide unread notification seeded
// for the sample account.
const invitationNotification = "ntf_01JC9UCE5V7X9Z1C4E6H1M4OQ"

// TestMCPNotificationsListAndMarkRead reads the seeded inbox and marks one
// notification read; a fresh account has nothing to mark.
func TestMCPNotificationsListAndMarkRead(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	session := connectMCP(t, createAccessToken(t, page, sampleAccount.Password, "inbox assistant"))

	type notification struct{ ID, Title, Status, Severity string }
	var inbox struct {
		Notifications []notification
		Total         int
		Unread        int
		Matching      int
	}
	callTool(t, session, "list_notifications", nil, &inbox)
	if inbox.Total < 4 || len(inbox.Notifications) != inbox.Total {
		t.Fatalf("list_notifications = %+v, want the seeded inbox", inbox)
	}
	var marked struct{ Marked int }
	callTool(t, session, "mark_notification_read", map[string]any{"notificationId": invitationNotification}, &marked)
	if marked.Marked != 1 {
		t.Fatalf("first mark_notification_read = %d, want one transition", marked.Marked)
	}
	callTool(t, session, "mark_notification_read", map[string]any{"notificationId": invitationNotification}, &marked)
	if marked.Marked != 0 {
		t.Errorf("repeated mark_notification_read = %d, want no transition", marked.Marked)
	}
	callTool(t, session, "list_notifications", map[string]any{"status": "unread"}, &inbox)
	if slices.ContainsFunc(inbox.Notifications, func(n notification) bool { return n.ID == invitationNotification }) {
		t.Errorf("unread notifications still include %s: %+v", invitationNotification, inbox.Notifications)
	}
	if inbox.Matching != inbox.Unread || slices.ContainsFunc(inbox.Notifications, func(n notification) bool { return !strings.EqualFold(n.Status, "unread") }) {
		t.Errorf("unread filter = %+v, want only unread notifications", inbox)
	}
	if message := callToolError(t, session, "mark_notification_read", map[string]any{"notificationId": "ntf_missing"}); !strings.Contains(message, "not found") {
		t.Errorf("unknown notification error = %q, want not found", message)
	}

	freshPage := newPage(t)
	registerAndOnboard(t, freshPage, newAccount("mcp-inbox"))
	fresh := connectMCP(t, createAccessToken(t, freshPage, journeyPassword, "empty inbox"))
	callTool(t, fresh, "mark_all_notifications_read", nil, &marked)
	if marked.Marked != 0 {
		t.Errorf("mark_all_notifications_read on an empty inbox marked %d", marked.Marked)
	}
}
