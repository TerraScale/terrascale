package notifications

import (
	"context"
	"strconv"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

type notificationView struct {
	ID        string    `json:"id"`
	Type      string    `json:"type,omitempty"`
	Title     string    `json:"title"`
	Body      string    `json:"body"`
	Status    string    `json:"status" jsonschema:"Unread or Read."`
	Severity  string    `json:"severity"`
	CreatedAt time.Time `json:"createdAt"`
}

type listNotificationsInput struct {
	core.Scope
	Status string `json:"status,omitempty" jsonschema:"unread lists only unread notifications; omit or use all for every notification."`
	Page   int    `json:"page,omitempty" jsonschema:"1-based page of 10 notifications. Defaults to 1."`
}

type notificationsOutput struct {
	Notifications []notificationView `json:"notifications"`
	Status        string             `json:"status"`
	Unread        int                `json:"unread"`
	Critical      int                `json:"critical" jsonschema:"Unread notifications with danger or critical severity."`
	Total         int                `json:"total"`
	Matching      int                `json:"matching" jsonschema:"Notifications matching the status filter across all pages."`
	Page          int                `json:"page"`
	PageCount     int                `json:"pageCount"`
}

type notificationInput struct {
	core.Scope
	NotificationID string `json:"notificationId" jsonschema:"Notification ID from list_notifications."`
}

type markedRead struct {
	Marked int `json:"marked" jsonschema:"Number of notifications that changed from unread to read."`
}

func mountTools(tools *core.Tools, service Service) {
	core.AddTool(tools, core.ToolSpec{
		Name: "list_notifications", Title: "List notifications", ReadOnly: true,
		Description: "List the notification inbox of an organization and project, newest first, with unread and critical counts.",
	}, func(ctx context.Context, p core.Principal, in listNotificationsInput) (notificationsOutput, error) {
		data, err := service.Inbox(ctx, p, in.Status, strconv.Itoa(in.Page))
		if err != nil {
			return notificationsOutput{}, err
		}
		out := notificationsOutput{
			Notifications: make([]notificationView, 0, len(data.Notifications)), Status: data.Status,
			Unread: data.Unread, Critical: data.Critical, Total: data.Total, Matching: data.FilteredCount,
			Page: data.Page, PageCount: data.PageCount,
		}
		for _, item := range data.Notifications {
			out.Notifications = append(out.Notifications, notificationView{
				ID: item.ID, Type: item.Type, Title: item.Title, Body: item.Body,
				Status: item.Status, Severity: item.Severity, CreatedAt: item.CreatedAt,
			})
		}
		return out, nil
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "mark_notification_read", Title: "Mark notification read", Idempotent: true,
		Description: "Mark one notification as read.",
	}, func(ctx context.Context, p core.Principal, in notificationInput) (markedRead, error) {
		marked, err := service.MarkRead(ctx, p, in.NotificationID)
		if err != nil {
			return markedRead{}, err
		}
		if marked {
			return markedRead{Marked: 1}, nil
		}
		return markedRead{}, nil
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "mark_all_notifications_read", Title: "Mark all notifications read", Idempotent: true,
		Description: "Mark every notification in an organization and project as read.",
	}, func(ctx context.Context, p core.Principal, _ core.Scope) (markedRead, error) {
		marked, err := service.MarkAllRead(ctx, p)
		return markedRead{Marked: marked}, err
	})
}
