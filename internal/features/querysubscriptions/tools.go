package querysubscriptions

import (
	"context"

	"terrascale.tech/dashboard/internal/core"
)

type attachSubscriptionInput struct {
	core.Scope
	DatabaseID string `json:"databaseId" jsonschema:"Database ID from list_databases."`
	Name       string `json:"name" jsonschema:"View name: a lowercase letter, then up to 63 lowercase letters, digits, hyphens, or underscores."`
	Query      string `json:"query" jsonschema:"SELECT * FROM collection [LIMIT 1-100]."`
	Transport  string `json:"transport" jsonschema:"Delivery transport: http or websocket."`
}

type subscriptionInput struct {
	core.Scope
	SubscriptionID string `json:"subscriptionId" jsonschema:"Configuration ID from list_query_subscriptions."`
}

type subscriptionList struct {
	Subscriptions []subscriptionView `json:"subscriptions"`
}

type subscriptionChange struct {
	SubscriptionID string `json:"subscriptionId"`
	Status         string `json:"status" jsonschema:"paused, saved after a resume, or detached."`
}

func mountTools(tools *core.Tools, service Service) {
	core.AddTool(tools, core.ToolSpec{
		Name: "list_query_subscriptions", Title: "List query subscriptions", ReadOnly: true,
		Description: "List the project's saved shared-result (query subscription) configurations.",
	}, func(ctx context.Context, p core.Principal, _ core.Scope) (subscriptionList, error) {
		list, err := service.List(ctx, p)
		return subscriptionList{Subscriptions: list}, err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "attach_query_subscription", Title: "Attach query subscription",
		Enums:       map[string][]string{"transport": {"http", "websocket"}},
		Description: "Save a shared-result configuration that publishes a query's results over HTTP or WebSocket. Requires write access.",
	}, func(ctx context.Context, p core.Principal, in attachSubscriptionInput) (subscriptionView, error) {
		return service.Attach(ctx, p, attachInput{DatabaseID: in.DatabaseID, Name: in.Name, Query: in.Query, Transport: in.Transport})
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "pause_query_subscription", Title: "Pause query subscription", Idempotent: true,
		Description: "Pause a shared-result configuration. Requires write access.",
	}, func(ctx context.Context, p core.Principal, in subscriptionInput) (subscriptionChange, error) {
		return subscriptionChange{SubscriptionID: in.SubscriptionID, Status: "paused"}, service.Pause(ctx, p, in.SubscriptionID)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "resume_query_subscription", Title: "Resume query subscription", Idempotent: true,
		Description: "Resume a paused shared-result configuration. Requires write access.",
	}, func(ctx context.Context, p core.Principal, in subscriptionInput) (subscriptionChange, error) {
		return subscriptionChange{SubscriptionID: in.SubscriptionID, Status: "saved"}, service.Resume(ctx, p, in.SubscriptionID)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "detach_query_subscription", Title: "Detach query subscription", Destructive: true,
		Description: "Delete a shared-result configuration. Requires write access.",
	}, func(ctx context.Context, p core.Principal, in subscriptionInput) (subscriptionChange, error) {
		return subscriptionChange{SubscriptionID: in.SubscriptionID, Status: "detached"}, service.Detach(ctx, p, in.SubscriptionID)
	})
}
