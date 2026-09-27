package payments

import (
	"context"

	"terrascale.tech/dashboard/internal/core"
)

type planInput struct {
	core.Scope
	Plan     string `json:"plan" jsonschema:"Plan key from list_plans, such as starter, pro, or business. change_plan and preview_plan_change also accept free for Personal workspaces."`
	Interval string `json:"interval,omitempty" jsonschema:"Billing interval: monthly or annual. Omit for free."`
}

type checkoutStarted struct {
	CheckoutURL string `json:"checkoutUrl" jsonschema:"URL the organization owner must open in a browser signed in to the dashboard to review and confirm payment. Relative URLs are dashboard pages."`
}

type checkoutCanceled struct {
	Canceled bool `json:"canceled"`
}

type planChanged struct {
	Outcome string `json:"outcome" jsonschema:"current: the plan was already active; changed: the upgrade applied now; pending: the upgrade waits for Stripe to confirm payment; scheduled: the change applies at renewal; undone: the scheduled change was canceled."`
}

type portalOpened struct {
	URL string `json:"url" jsonschema:"Billing portal URL for the organization owner to open in a browser. Relative URLs are dashboard pages."`
}

func mountTools(tools *core.Tools, service Service) {
	core.AddTool(tools, core.ToolSpec{
		Name: "create_checkout", Title: "Start plan checkout", OpenWorld: true,
		Description: "Start a checkout that subscribes an organization without a subscription to a paid plan. Returns the URL the owner must open in a browser to pay; this tool never completes a payment. Owner only.",
	}, func(ctx context.Context, p core.Principal, in planInput) (checkoutStarted, error) {
		location, err := service.StartCheckout(ctx, p, in.Plan, in.Interval, "")
		return checkoutStarted{CheckoutURL: location}, err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "cancel_checkout", Title: "Cancel plan checkout", OpenWorld: true,
		Description: "Cancel the organization's checkout in progress without changing its plan. Owner only.",
	}, func(ctx context.Context, p core.Principal, _ core.Scope) (checkoutCanceled, error) {
		err := service.CancelCheckout(ctx, p)
		return checkoutCanceled{Canceled: err == nil}, err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "preview_plan_change", Title: "Preview plan change", ReadOnly: true, OpenWorld: true,
		Description: "Estimate the charge and effective date of changing a subscribed organization's plan, without changing it. Owner only.",
	}, func(ctx context.Context, p core.Principal, in planInput) (PlanChangePreview, error) {
		return service.PreviewPlanChange(ctx, p, in.Plan, in.Interval)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "change_plan", Title: "Change plan", OpenWorld: true, Idempotent: true,
		Description: "Change a subscribed organization's plan. Upgrades apply now (charging a prorated amount); downgrades and switching a Personal workspace to free are scheduled for renewal and can be undone with undo_plan_change. Owner only.",
	}, func(ctx context.Context, p core.Principal, in planInput) (planChanged, error) {
		outcome, err := service.ChangePlan(ctx, p, in.Plan, in.Interval)
		return planChanged{Outcome: outcome}, err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "undo_plan_change", Title: "Undo scheduled plan change", OpenWorld: true, Idempotent: true,
		Description: "Cancel a plan change scheduled for renewal so the current plan continues. Owner only.",
	}, func(ctx context.Context, p core.Principal, _ core.Scope) (planChanged, error) {
		outcome, err := service.UndoPlanChange(ctx, p)
		return planChanged{Outcome: outcome}, err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "get_billing_portal_url", Title: "Open billing portal", OpenWorld: true,
		Description: "Create a billing portal session where the owner updates payment methods and downloads invoices. Returns the URL to open in a browser. Owner only.",
	}, func(ctx context.Context, p core.Principal, _ core.Scope) (portalOpened, error) {
		location, err := service.PortalURL(ctx, p)
		return portalOpened{URL: location}, err
	})
}
