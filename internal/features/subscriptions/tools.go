package subscriptions

import (
	"cmp"
	"context"
	"fmt"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

type planSummary struct {
	Key                string   `json:"key"`
	Name               string   `json:"name"`
	MonthlyPriceCents  int64    `json:"monthlyPriceCents"`
	AnnualPriceCents   int64    `json:"annualPriceCents" jsonschema:"Price for a year billed annually; 0 when the plan bills monthly only."`
	TrialDays          int      `json:"trialDays"`
	MaxDatabases       int      `json:"maxDatabases" jsonschema:"Database limit; 0 means unlimited."`
	MaxMembers         int      `json:"maxMembers" jsonschema:"Member limit; 0 means unlimited."`
	OperationsPerMonth int64    `json:"operationsPerMonth"`
	Features           []string `json:"features"`
	Current            bool     `json:"current" jsonschema:"Whether the organization is on this plan."`
}

type planList struct {
	Plans           []planSummary `json:"plans"`
	CurrentPlanKey  string        `json:"currentPlanKey"`
	CurrentInterval string        `json:"currentInterval,omitempty"`
}

type paymentMethod struct {
	Validated bool   `json:"validated"`
	Brand     string `json:"brand,omitempty"`
	LastFour  string `json:"lastFour,omitempty"`
	Expiry    string `json:"expiry,omitempty"`
}

type planChange struct {
	PlanKey     string    `json:"planKey"`
	Interval    string    `json:"interval,omitempty"`
	EffectiveAt time.Time `json:"effectiveAt,omitzero"`
}

type billingSummary struct {
	OrganizationID     string         `json:"organizationId"`
	OrganizationName   string         `json:"organizationName"`
	OrganizationKind   string         `json:"organizationKind"`
	PlanKey            string         `json:"planKey"`
	PlanName           string         `json:"planName"`
	Interval           string         `json:"interval,omitempty"`
	Status             string         `json:"status" jsonschema:"Billing status as the billing page shows it, such as Active, Trialing, or Payment required."`
	SubscriptionStatus string         `json:"subscriptionStatus,omitempty"`
	HasSubscription    bool           `json:"hasSubscription" jsonschema:"Whether a live subscription exists; plan changes need one, otherwise use create_checkout."`
	RenewalAt          time.Time      `json:"renewalAt,omitzero"`
	TrialUsed          bool           `json:"trialUsed"`
	CheckoutInProgress bool           `json:"checkoutInProgress"`
	ScheduledChange    *planChange    `json:"scheduledChange,omitempty" jsonschema:"Plan change that applies at renewal; undo it with undo_plan_change."`
	PendingChange      *planChange    `json:"pendingChange,omitempty" jsonschema:"Upgrade waiting for Stripe to confirm payment."`
	PaymentMethod      *paymentMethod `json:"paymentMethod,omitempty" jsonschema:"Payment method summary, shown to the owner only."`
	CanManage          bool           `json:"canManage" jsonschema:"Whether the caller owns the organization and may change billing."`
}

type invoiceInput struct {
	core.Scope
	Status string `json:"status,omitempty" jsonschema:"Only invoices with this status: paid, open, or void."`
	Issued string `json:"issued,omitempty" jsonschema:"Only invoices issued in the last 3m, 6m, or 12m, or in a four-digit year such as 2026."`
	Page   int    `json:"page,omitempty" jsonschema:"1-based page of 10 invoices, newest first."`
}

type invoiceView struct {
	ID          string    `json:"id"`
	Number      string    `json:"number"`
	Status      string    `json:"status"`
	AmountCents int64     `json:"amountCents"`
	Currency    string    `json:"currency"`
	CreatedAt   time.Time `json:"createdAt"`
	PeriodStart time.Time `json:"periodStart,omitzero"`
	PeriodEnd   time.Time `json:"periodEnd,omitzero"`
	HostedURL   string    `json:"hostedUrl,omitempty"`
	PDFURL      string    `json:"pdfUrl,omitempty"`
}

type invoiceList struct {
	Invoices  []invoiceView `json:"invoices"`
	Page      int           `json:"page"`
	PageCount int           `json:"pageCount"`
	Total     int           `json:"total"`
}

// PlanList lists the purchasable plans, marking the organization's current one.
func (service BillingService) PlanList(ctx context.Context, principal core.Principal) (planList, error) {
	view, err := service.Snapshot(ctx, principal)
	if err != nil {
		return planList{}, err
	}
	current := normalizePlanKey(cmp.Or(view.Organization.Billing.PlanKey, view.Plan.Key))
	list := planList{CurrentPlanKey: current, CurrentInterval: view.Organization.Billing.Interval, Plans: make([]planSummary, 0, len(view.Plans))}
	for _, plan := range view.Plans {
		key := normalizePlanKey(plan.Key)
		list.Plans = append(list.Plans, planSummary{
			Key: key, Name: plan.Name, MonthlyPriceCents: plan.PriceCents, AnnualPriceCents: plan.AnnualPriceCents,
			TrialDays: plan.TrialDays, MaxDatabases: plan.MaxDatabases, MaxMembers: plan.MaxMembers,
			OperationsPerMonth: plan.OperationsPerMonth, Features: planFeatures(plan), Current: key == current,
		})
	}
	return list, nil
}

// Summary describes the organization's plan, subscription, and pending
// changes as the billing page shows them.
func (service BillingService) Summary(ctx context.Context, principal core.Principal) (billingSummary, error) {
	view, err := service.Snapshot(ctx, principal)
	if err != nil {
		return billingSummary{}, err
	}
	org, billing := view.Organization, view.Organization.Billing
	summary := billingSummary{
		OrganizationID: org.ID, OrganizationName: org.Name, OrganizationKind: org.Kind,
		PlanKey: normalizePlanKey(cmp.Or(billing.PlanKey, view.Plan.Key)), PlanName: cmp.Or(view.Plan.Name, "Free"),
		Interval: billing.Interval, Status: billingLabel(org), SubscriptionStatus: billing.Status,
		HasSubscription: billing.SubscriptionID != "" && billing.Status != "canceled" && billing.Status != "incomplete_expired",
		RenewalAt:       org.BillingRenewal, TrialUsed: billing.TrialUsed, CheckoutInProgress: billing.CheckoutID != "",
		CanManage: org.OwnerUserID == principal.UserID,
	}
	if billing.ScheduledPlanKey != "" {
		summary.ScheduledChange = &planChange{PlanKey: billing.ScheduledPlanKey, Interval: billing.ScheduledInterval, EffectiveAt: billing.ScheduledAt}
	}
	if billing.PendingPlanKey != "" {
		summary.PendingChange = &planChange{PlanKey: billing.PendingPlanKey, Interval: billing.PendingInterval}
	}
	if summary.CanManage {
		summary.PaymentMethod = &paymentMethod{
			Validated: org.PaymentMethodValidated, Brand: billing.PaymentMethodBrand,
			LastFour: billing.PaymentMethodLastFour, Expiry: billing.PaymentMethodExpiry,
		}
	}
	return summary, nil
}

// Invoices pages the organization's invoices newest first, filtered like the
// billing tab.
func (service BillingService) Invoices(ctx context.Context, principal core.Principal, status, issued string, page int) (invoiceList, error) {
	view, err := service.Snapshot(ctx, principal)
	if err != nil {
		return invoiceList{}, err
	}
	found, err := PageInvoices(view.Organization.Billing.Invoices, status, issued, page, service.now())
	if err != nil {
		return invoiceList{}, err
	}
	list := invoiceList{Page: found.Page, PageCount: found.PageCount, Total: found.Total, Invoices: make([]invoiceView, 0, len(found.Invoices))}
	for _, invoice := range found.Invoices {
		list.Invoices = append(list.Invoices, invoiceView{
			ID: invoice.ID, Number: invoice.Number, Status: invoice.Status, AmountCents: invoice.AmountCents,
			Currency: invoice.Currency, CreatedAt: invoice.CreatedAt, PeriodStart: invoice.PeriodStart, PeriodEnd: invoice.PeriodEnd,
			HostedURL: InvoiceURL(invoice.HostedURL), PDFURL: InvoiceURL(invoice.PDFURL),
		})
	}
	return list, nil
}

func mountTools(tools *core.Tools, service BillingService) {
	core.AddTool(tools, core.ToolSpec{
		Name: "list_plans", Title: "List plans", ReadOnly: true,
		Description: "List the paid plans an organization can subscribe to, with prices in cents, limits, and which plan is current.",
	}, func(ctx context.Context, p core.Principal, _ core.Scope) (planList, error) {
		return service.PlanList(ctx, p)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "get_billing", Title: "Get billing", ReadOnly: true,
		Description: "Show an organization's plan, billing interval and status, renewal date, scheduled or pending plan changes, checkout state, and (for the owner) payment method summary.",
	}, func(ctx context.Context, p core.Principal, _ core.Scope) (billingSummary, error) {
		return service.Summary(ctx, p)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "list_invoices", Title: "List invoices", ReadOnly: true,
		Description: fmt.Sprintf("List an organization's invoices newest first, %d per page, optionally filtered by status and issue date.", InvoicePageSize),
	}, func(ctx context.Context, p core.Principal, in invoiceInput) (invoiceList, error) {
		return service.Invoices(ctx, p, in.Status, in.Issued, in.Page)
	})
}
